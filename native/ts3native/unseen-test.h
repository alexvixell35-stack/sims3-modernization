// TestUnseen ([UnseenProbe], unseen.cpp, docs/143). Included by selftest.cpp. Fakes for every game call; no game code runs.
struct UnFakeObj { float pos[4]; bool sim; u8 pad[0x30 - 20]; void* parent; };  // as a transform: +0x30 = parent
static const DWORD kUnBitT = 1, kUnBitS = 0x80;
static int t_unSeenCls, t_unCalls;
static void* __fastcall UnFakeComp(void* obj, void*, DWORD bit) {
    UnFakeObj* o = (UnFakeObj*)obj;
    return bit == kUnBitT ? obj : bit == kUnBitS && o->sim ? obj : NULL;
}
static float* __fastcall UnFakePos(void* tc, void*, float* out) { memcpy(out, ((UnFakeObj*)tc)->pos, 16); return out; }
static int g_unFakeFrustum;
static void* __cdecl UnFakeFrustum() { return &g_unFakeFrustum; }
static u8 __fastcall UnFakeIn(const void*, void*, const float* s) { return s[0] > -10 && s[0] < 10; }  // on screen: |x| < 10
static float g_unFakeCam[2][3];
static float* __cdecl UnFakeCamPos(float* out) { memcpy(out, g_unFakeCam[0], 12); return out; }
static float* __cdecl UnFakeCamTgt(float* out) { memcpy(out, g_unFakeCam[1], 12); return out; }
static u8* t_unSeenAgent;
static float t_unSeenPos0;
static void __fastcall UnFakeAgent(void*, void*, float) {
    t_unSeenCls = t_unCls, t_unCalls++, t_unSeenAgent = t_unAgent, t_unSeenPos0 = t_unPos[0];
    UnAcq(0x0085FE10);        // steering acquire inside the update
    UnWait(0x00846D21, 700);  // avoidance wait inside the update
}
static int t_unUsfCls;
static void __fastcall UnFakeUsf(void*, void*, void*, DWORD, DWORD, DWORD) { t_unUsfCls = t_unCls; UnWait(0x00833593, 40); }
static u8 g_unFakeRoute[0x200];
static const u8* __fastcall UnFakeRouteLookup(const void*, void*, DWORD handle) { return handle == 7 ? g_unFakeRoute : NULL; }
static UnFakeObj* g_unFakeFound;
static void* __fastcall UnFakeFind(void*, void*, DWORD lo, DWORD hi) { return lo == 0x1234 && hi == 5 ? g_unFakeFound : NULL; }

static void TestUnseen() {
    Ts3nCounters& k = ts3native_counters;
    static_assert(offsetof(Ts3nCounters, unReady) == 0x32B4 && offsetof(Ts3nCounters, unAgents) == 0x32C8 &&
                  offsetof(Ts3nCounters, unAgentMaxUs) == 0x32E8 && offsetof(Ts3nCounters, unAcq) == 0x32F0 &&
                  offsetof(Ts3nCounters, unWaitN) == 0x3314 && offsetof(Ts3nCounters, unWaitUs) == 0x3344 &&
                  offsetof(Ts3nCounters, unFp) == 0x3374 && offsetof(Ts3nCounters, unPlan) == 0x338C &&
                  offsetof(Ts3nCounters, unPlanUs) == 0x33A4 && offsetof(Ts3nCounters, unReachX10) == 0x33BC &&
                  offsetof(Ts3nCounters, unEaOff) == 0x33C0 &&
                  offsetof(Ts3nCounters, utMode) == 0x33C8 && offsetof(Ts3nCounters, utWhy) == 0x33E0 &&
                  offsetof(Ts3nCounters, utPathUs) == 0x3414 && offsetof(Ts3nCounters, utViewerBad) == 0x3420 &&
                  sizeof(Ts3nCounters) == 6116 * 4, "counters v79 layout after v78 (docs/registry.md)");
    assert(k.version == 912);
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    // swap every game pointer for a fake
    const UnAgentFn agentSave = g_unAgentOrig;
    const UnUsfFn usfSave = g_unUsfOrig;
    const UnCompFn compSave = g_unComp;
    const UnPosFn posSave = g_unPos;
    const UnFrustumFn frSave = g_unFrustum;
    const UnInFrustumFn inSave = g_unInFrustum;
    const UnCamFn cpSave = g_unCamPos, ctSave = g_unCamTgt;
    const UnFindFn findSave = g_unFind;
    const AsRouteFn routeSave = g_unRoute;
    const DWORD *btSave = g_unBitTransform, *bsSave = g_unBitSim;
    u8* const* sysSave = g_unLocoSys;
    void* const* mgrSave = g_unObjMgr;
    const u8* const* rmSave = g_unRmSlot;
    const DWORD tidSave = g_unSimTid;
    g_unAgentOrig = UnFakeAgent, g_unUsfOrig = UnFakeUsf, g_unComp = UnFakeComp, g_unPos = UnFakePos, g_unFrustum = UnFakeFrustum;
    g_unInFrustum = UnFakeIn, g_unCamPos = UnFakeCamPos, g_unCamTgt = UnFakeCamTgt, g_unFind = UnFakeFind, g_unRoute = UnFakeRouteLookup;
    g_unBitTransform = &kUnBitT, g_unBitSim = &kUnBitS;

    // 1. the rule: reach = max(80, 2 |cam y - target y|) around target or camera; on screen always visible
    g_unFakeCam[0][0] = 0, g_unFakeCam[0][1] = 30, g_unFakeCam[0][2] = 0;  // camera 30 m above the target: reach 80
    memset(g_unFakeCam[1], 0, 12);
    UnRefreshCamera();
    assert(g_unCam.ok && g_unCam.reach == 80.0f && k.unReachX10 == 800);
    const float pNear[3] = {50, 0, 0}, pFar[3] = {100, 0, 0}, pFarCam[3] = {0, 100, 0};
    assert(UnClassify(pNear, false) == 0 && UnClassify(pFar, false) == 1 && UnClassify(pFar, true) == 0);
    assert(UnClassify(pFarCam, false) == 0);  // 100 m from the target, 70 m from the camera
    g_unFakeCam[0][1] = 60;                  // zoomed out: reach 120
    UnRefreshCamera();
    assert(g_unCam.reach == 120.0f && UnClassify(pFar, false) == 0);
    g_unFakeCam[0][1] = 30;
    UnRefreshCamera();

    // 2. agent updates: a visible Sim (list head) and an unseen car; the waits / acquires inside land in their class
    __declspec(align(16)) static UnFakeObj sim = {{2, 0, 0, 1}, true}, car = {{300, 0, 0, 1}, false};
    static u8 agentA[0x110], agentB[0x110], sys[0x50];
    *(void**)(agentA + 4) = &sim, *(void**)(agentB + 4) = &car;
    agentA[0xC8] = 1, agentB[0xC8] = 0;  // EA's own byte: A on screen, B off
    *(u8**)(sys + 0x40) = agentA + 0x40;
    static u8* sysPtr = sys;
    g_unLocoSys = &sysPtr;
    static Ts3nCounters before;
    memcpy(&before, &k, sizeof(k));
    UnAgentThunk(agentA, NULL, 0.033f);
    assert(t_unSeenCls == 1 && t_unCls == 0 && g_unSimTid == GetCurrentThreadId());
    assert(t_unSeenAgent == agentA && t_unSeenPos0 == 2 && t_unAgent == NULL);  // [UnseenThrottle] context, cleared after
    UnAgentThunk(agentB, NULL, 0.033f);
    assert(t_unSeenCls == 2 && t_unCalls == 2);
    assert(k.unPasses == before.unPasses + 1);  // only the head started a pass
    assert(k.unEaOff[0] == before.unEaOff[0] && k.unEaOff[1] == before.unEaOff[1] + 1);
    assert(k.unAgents[0][0] == before.unAgents[0][0] + 1 && k.unAgents[1][1] == before.unAgents[1][1] + 1);
    assert(k.unAgents[0][1] == before.unAgents[0][1] && k.unAgents[1][0] == before.unAgents[1][0]);
    assert(k.unAcq[0][0] == before.unAcq[0][0] + 1 && k.unAcq[1][0] == before.unAcq[1][0] + 1);
    assert(k.unWaitN[0][1] == before.unWaitN[0][1] + 1 && k.unWaitUs[1][1] == before.unWaitUs[1][1] + 700);
    // outside an update: class 2; a non-steering key is "other"
    UnAcq(0x004E1234);
    UnWait(0x0062EF7A, 50);
    assert(k.unAcq[2][2] == before.unAcq[2][2] + 1 && k.unWaitN[2][3] == before.unWaitN[2][3] + 1);
    // the car comes into view: one flip
    car.pos[0] = 3;
    UnAgentThunk(agentB, NULL, 0.033f);
    assert(t_unSeenCls == 1 && k.unFlips == before.unFlips + 1 && k.unAgents[0][1] == before.unAgents[0][1] + 1);
    // no owner: run as is, not split
    *(void**)(agentB + 4) = NULL;
    UnAgentThunk(agentB, NULL, 0.033f);
    assert(t_unCalls == 4 && t_unSeenCls == 0 && k.unSkipped == before.unSkipped + 1);
    *(void**)(agentB + 4) = &car, car.pos[0] = 300;
    // no camera: every agent counts as visible (never unseen by error)
    g_unCam.ok = false;
    assert(UnClassify(pFar, false) == 0);
    UnRefreshCamera();

    // 3. footprint updates: classed by the object, the update's own wait inside the same class
    t_unUsfCls = -1;
    UnUsfHook(NULL, NULL, &car, 1, 0, 0);
    assert(t_unUsfCls == 2 && t_unCls == 0 && k.unFp[1] == before.unFp[1] + 1 && k.unWaitN[1][2] == before.unWaitN[1][2] + 1);
    UnUsfHook(NULL, NULL, NULL, 1, 0, 0);  // no object: class 2 (unknown), no class while it runs
    assert(t_unUsfCls == 0 && k.unFp[2] == before.unFp[2] + 1 && k.unWaitN[2][2] == before.unWaitN[2][2] + 1);
    // with design U on 0x833560 the probe sits behind design U's stub: design U's way to the body reaches the probe first
    const UsfFn duSave = g_usfOrig;
    g_usfOrig = (UsfFn)UnFakeUsf;  // stands for design U's trampoline
    g_unUsfOrig = g_usfOrig, g_usfOrig = UnUsfHook;  // as ApplyUnseenProbe with usfReady
    t_unUsfCls = -1;
    g_usfOrig(NULL, NULL, &car, 0, 1, 0);  // a deferred record applied at a drain: classed by the object
    assert(t_unUsfCls == 2 && k.unFp[1] == before.unFp[1] + 2);
    g_usfOrig = duSave, g_unUsfOrig = UnFakeUsf;

    // 4. plans: car-only route option (0x20 at route+0x158+0x28), follower id at route+0x40/+0x44
    static const u8 rmDummy = 0;
    static const u8* rmPtr = &rmDummy;
    static void* mgrPtr = (void*)&rmDummy;
    g_unRmSlot = &rmPtr, g_unObjMgr = &mgrPtr, g_unFakeFound = &car;
    memset(g_unFakeRoute, 0, sizeof(g_unFakeRoute));
    *(DWORD*)(g_unFakeRoute + 0x158 + 0x28) = 0x20 | 0x40 | 0x80;
    *(DWORD*)(g_unFakeRoute + 0x40) = 0x1234, *(DWORD*)(g_unFakeRoute + 0x44) = 5;
    int kind = -1, cls = -1;
    UnPlanClass(7, &kind, &cls);
    assert(kind == 1 && cls == 1);
    *(DWORD*)(g_unFakeRoute + 0x158 + 0x28) = 0x10;  // a Sim that may drive: not car-only
    g_unFakeFound = &sim;
    UnPlanClass(7, &kind, &cls);
    assert(kind == 0 && cls == 0);
    UnPlanClass(8, &kind, &cls);  // unknown handle
    assert(kind == 0 && cls == 2);

    g_unAgentOrig = agentSave, g_unUsfOrig = usfSave, g_unComp = compSave, g_unPos = posSave, g_unFrustum = frSave;
    g_unInFrustum = inSave, g_unCamPos = cpSave, g_unCamTgt = ctSave, g_unFind = findSave, g_unRoute = routeSave;
    g_unBitTransform = btSave, g_unBitSim = bsSave, g_unLocoSys = sysSave, g_unObjMgr = mgrSave, g_unRmSlot = rmSave;
    g_unSimTid = tidSave;
    g_unCam.ok = false, g_unCam.at.QuadPart = 0;
    memset(g_unLast, 0, sizeof(g_unLast));
    printf("UnseenProbe: visible / unseen classifier (ts3fixes reach rule), per-class agent updates, keyed acquires and waits, "
           "footprint updates (own detour and behind design U) and car-only plans: ok\n");
}

// ---- TestUnseenThrottle ([UnseenThrottle] phase 1, docs/143 §6): stagger, snap-back rules, empty-result semantics ----
static float g_utFRest = 100, g_utFLen = 50, g_utFProg = 20;
static bool g_utFFault;
static int g_utFListCalls;
static unsigned __int64 __fastcall UtFakeCur(void*, void*) { return 0x0000000500000007ull; }
static float __cdecl UtFakeRemain(void* path, DWORD lo, DWORD hi) {
    if (g_utFFault) return *(volatile float*)(uintptr_t)8;  // access violation: the caught path fault
    assert(path && lo == 7 && hi == 5);
    InterlockedDecrement((volatile LONG*)((u8*)path + 4));  // the game's 0x8469F0 releases its by-value path ref
    return g_utFRest;
}
static float __fastcall UtFakeLen(void*, void*) { return g_utFLen; }
static float __fastcall UtFakeProg(void*, void*, DWORD lo, DWORD hi) { assert(lo == 7 && hi == 5); return g_utFProg; }
static void __cdecl UtFakeList(void* part, void* path, void* vec, DWORD a3, DWORD a4) {
    assert(part && path && vec && a3 == 0 && a4 == 0);
    g_utFListCalls++;
    ((DWORD*)vec)[0] = 0x100, ((DWORD*)vec)[1] = 0x108;  // one obstacle id
}
static void __cdecl UtFakeListAny(void*, void*, void*, DWORD, DWORD) { g_utFListCalls++; }
static u8 UtMsg(DWORD code, DWORD v) { return UtViewerMsg(v, 0x82000000 | code << 16); }
static u8 UtMsgF(DWORD code, float f) { DWORD v; memcpy(&v, &f, 4); return UtMsg(code, v); }
static void UtPush(const float (*p)[3], int n, int endCount) {
    assert(UtMsg(1, n));
    for (int i = 0; i < n; i++) assert(UtMsgF(2, p[i][0]) && UtMsgF(3, p[i][1]) && UtMsgF(4, p[i][2]));
    UtMsg(5, endCount);
}

static void TestUnseenThrottle() {
    Ts3nCounters& k = ts3native_counters;
    const UtListFn origSave = g_utOrig;
    const UtRemainFn remSave = g_utRemain;
    const LONG modeSave = k.utMode, everySave = k.utEvery, passSave = k.unPasses;
    const bool onSave = g_utOn, needSave = g_utNeedViewers;
    g_utOrig = UtFakeList, g_utRemain = UtFakeRemain;
    k.utEvery = 4, g_utNeedViewers = true;
    static void* pathVt[0x130 / 4];
    static void* partVt[8];
    pathVt[0x12C / 4] = (void*)UtFakeLen, pathVt[0xA4 / 4] = (void*)UtFakeProg, partVt[0x10 / 4] = (void*)UtFakeCur;
    static void* path[2] = {pathVt, (void*)1};  // +4 = intrusive refcount, one ref held by the caller
    static u8 agent[0x110], agent2[0x110], owner[0x10], owner2[0x10];
    *(void***)(agent + 8) = partVt, *(void***)(agent2 + 8) = partVt;
    *(u8**)(agent + 4) = owner, *(u8**)(agent2 + 4) = owner2;
    *(DWORD*)(owner + 8) = 1, *(DWORD*)(owner2 + 8) = 2;  // owner ids: the stagger key
    *(DWORD*)(agent + 0x94) = 1, *(DWORD*)(agent2 + 0x94) = 1;
    memset(g_utPrev, 0, sizeof(g_utPrev));
    static Ts3nCounters b;
    memcpy(&b, &k, sizeof(k));
    // one locomotion call as the game makes it inside agent `a`'s update; returns 1 = ran (vector filled), 0 = skipped
    auto call = [&](u8* a, int cls, void* part) -> int {
        t_unAgent = a, t_unCls = cls;
        DWORD vec[4] = {0, 0, 0, 0x1004310};  // as zeroed at 0x957388..0x957396
        const int before = g_utFListCalls;
        UtListThunk(part, path, vec, 0, 0);
        t_unAgent = NULL, t_unCls = 0;
        assert(path[1] == (void*)1);  // path refcount unchanged (0.36.13 dropped one per path test: the bp-ut-play hang)
        const bool ran = g_utFListCalls != before;
        assert(ran ? vec[0] == 0x100 : vec[0] == 0 && vec[1] == 0 && vec[2] == 0 && vec[3] == 0x1004310);  // skipped = empty
        return ran ? 1 : 0;
    };
    const float far1[1][3] = {{1000, 0, 1000}}, near1[1][3] = {{10, 0, 0}};
    memset(t_unPos, 0, sizeof(t_unPos));

    // 1. viewer channel: refused while not installed; sequence and count checked; a good table goes live
    g_utOn = false;
    assert(UtMsg(1, 1) == 0);
    {  // docs/249: the held table without [UnseenThrottle]: refused with nobody to read it, live for [UnseenAnim]; both id halves match
        const bool aaSave = g_aaOn;
        const auto heldSave = g_utHeld;
        g_aaOn = false, g_utHeld.n = 0;
        assert(UtMsg(12, 0) == 0 && g_utHeld.n == 0);
        g_aaOn = true;
        assert(UtMsg(12, 0) && UtMsg(13, 0x24E62D10) && UtMsg(14, 0xC49C0022) && UtMsg(15, 1) && g_utHeld.n == 1);
        static DWORD idA[4], idLo[4], idHi[4];
        idA[2] = 0x24E62D10, idA[3] = 0xC49C0022;
        idLo[2] = 0x24E62D11, idLo[3] = 0xC49C0022;
        idHi[2] = 0x24E62D10, idHi[3] = 0xC49C0023;
        assert(UtHeldObj(idA) && !UtHeldObj(idLo) && !UtHeldObj(idHi));
        assert(UtMsg(6, 0) == 0 && UtMsg(19, 0) == 0);  // only the table codes opened up: the watch and skip queries stay throttle-only
        g_aaOn = aaSave, g_utHeld = heldSave;
    }
    g_utOn = true;
    g_utView.at = 0;
    UtPush(far1, 1, 2);  // count mismatch: refused, table stays stale
    assert(k.utViewerBad == b.utViewerBad + 1 && !g_utView.at);
    assert(UtMsgF(4, 1.0f) == 0 && k.utViewerBad == b.utViewerBad + 2);  // z outside BEGIN .. END
    // 2. no fresh table: every call runs (NeedViewers=1)
    k.utMode = 1, k.unPasses = 0;
    assert(call(agent, 2, agent + 8) == 1 && k.utWhy[UTW_NOVIEW] == b.utWhy[UTW_NOVIEW] + 1);
    UtPush(far1, 1, 1);
    assert(k.utViewers == 1 && k.utViewerPushes == b.utViewerPushes + 1 && (k.utReady & 2) && g_utView.at);

    // 3. stagger: after the first tick following a hold, agent 1 (id 1) runs at passes 3, 7; agent 2 (id 2) at 2, 6
    assert(call(agent, 2, agent + 8) == 1 && k.utWhy[UTW_AFTER] == b.utWhy[UTW_AFTER] + 1);  // NOVIEW held last time
    assert(call(agent2, 2, agent2 + 8) == 1 && k.utWhy[UTW_AFTER] == b.utWhy[UTW_AFTER] + 2);  // not seen before
    int runs1 = 0, runs2 = 0, mask1 = 0, mask2 = 0;
    for (int pass = 1; pass <= 8; pass++) {
        k.unPasses = pass;
        const int r1 = call(agent, 2, agent + 8), r2 = call(agent2, 2, agent2 + 8);
        runs1 += r1, runs2 += r2, mask1 |= r1 << pass, mask2 |= r2 << pass;
    }
    assert(runs1 == 2 && runs2 == 2 && mask1 == ((1 << 3) | (1 << 7)) && mask2 == ((1 << 2) | (1 << 6)));
    assert(k.utSkipped == b.utSkipped + 12 && k.utWhy[UTW_TURN] == b.utWhy[UTW_TURN] + 4);

    // 4. snap-back rules on a would-skip pass (pass 1: neither agent's turn); each held reason makes the next call run too
    k.unPasses = 1;
    auto expect = [&](int why) {
        const LONG n = k.utWhy[why];
        assert(call(agent, 2, agent + 8) == 1 && k.utWhy[why] == n + 1);
    };
    auto settle = [&]() {  // the tick after: runs once more (AFTER), then skips again
        const LONG n = k.utWhy[UTW_AFTER];
        assert(call(agent, 2, agent + 8) == 1 && k.utWhy[UTW_AFTER] == n + 1);
        assert(call(agent, 2, agent + 8) == 0);
    };
    g_utFRest = 10, expect(UTW_NEAR), g_utFRest = 100, settle();       // near goal (<= 15 m)
    g_utFRest = NAN, expect(UTW_NEAR), g_utFRest = 100, settle();      // NaN never skips
    g_utFProg = 2, expect(UTW_PORTAL), g_utFProg = 20, settle();       // a portal just crossed
    g_utFProg = 48, expect(UTW_PORTAL), g_utFProg = 20, settle();      // a portal ahead (50 - 48 <= 3 m)
    {  // a fault in the path tests runs the call; the ref added before the faulting call is leaked, never released twice
        g_utFFault = true;
        t_unAgent = agent, t_unCls = 2;
        DWORD vec[4] = {};
        const LONG n = k.utWhy[UTW_PATH];
        UtListThunk(agent + 8, path, vec, 0, 0);
        t_unAgent = NULL, t_unCls = 0;
        assert(k.utWhy[UTW_PATH] == n + 1 && path[1] == (void*)2);
        path[1] = (void*)1, g_utFFault = false, settle();
    }
    {
        t_unAgent = agent, t_unCls = 2;
        DWORD vec[4] = {};
        const LONG n = k.utWhy[UTW_PATH];
        g_utOrig = UtFakeListAny;
        UtListThunk(agent + 8, NULL, vec, 0, 0);  // no path: runs
        g_utOrig = UtFakeList;
        t_unAgent = NULL, t_unCls = 0;
        assert(k.utWhy[UTW_PATH] == n + 1);
        settle();
    }
    { const LONG n = k.utWhy[UTW_VISIBLE]; assert(call(agent, 1, agent + 8) == 1 && k.utWhy[UTW_VISIBLE] == n + 1); settle(); }
    agent[0xC8] = 1, expect(UTW_EAON), agent[0xC8] = 0, settle();
    *(DWORD*)(agent + 0x94) = 4, expect(UTW_STATE), *(DWORD*)(agent + 0x94) = 1, settle();  // paddleboat: not throttled
    { const LONG n = k.utWhy[UTW_LINKED]; assert(call(agent, 2, agent2 + 8) == 1 && k.utWhy[UTW_LINKED] == n + 1); settle(); }
    UtPush(near1, 1, 1), expect(UTW_VIEWER), UtPush(far1, 1, 1), settle();  // a household Sim within 80 m
    g_utView.at = GetTickCount() - 5000, expect(UTW_NOVIEW), UtPush(far1, 1, 1), settle();  // stale table
    g_utNeedViewers = false, g_utView.at = 0;
    assert(call(agent, 2, agent + 8) == 0);  // NeedViewers=0: a stale table is no reason
    g_utNeedViewers = true, UtPush(far1, 1, 1);
    // no agent context (update not classified) and the runtime switch off: every call runs
    { const LONG n = k.utWhy[UTW_NOAGENT]; assert(call(NULL, 0, agent + 8) == 1 && k.utWhy[UTW_NOAGENT] == n + 1); }
    k.utMode = 0;
    { const LONG n = k.utWhy[UTW_OFF]; assert(call(agent, 2, agent + 8) == 1 && k.utWhy[UTW_OFF] == n + 1); }
    k.utMode = 1;
    assert(k.utCalls - b.utCalls == k.utRun - b.utRun + k.utSkipped - b.utSkipped);
    assert(path[1] == (void*)1);  // path references balanced after every phase-1 row
    // v83: every run returned one obstacle (the fake), so later skips of these unseen agents count as deferred notices
    assert(k.utListNotices > b.utListNotices && k.utDefer[0] > b.utDefer[0] && k.utDefer[0] - b.utDefer[0] <= k.utSkipped - b.utSkipped);

    g_utOrig = origSave, g_utRemain = remSave;
    memset(g_utDg, 0, sizeof(g_utDg));
    k.utMode = modeSave, k.utEvery = everySave, k.unPasses = passSave;
    g_utOn = onSave, g_utNeedViewers = needSave;
    g_utView.at = 0, g_utView.n = 0, g_utInOpen = false;
    memset(g_utPrev, 0, sizeof(g_utPrev));
    printf("UnseenThrottle: viewer channel, stagger (each agent 1 call in 4, spread by id), snap-back rules (visible, EA byte, "
           "state, linked, viewer, stale table, near goal, portal, path fault, first tick after), skipped call = empty vector: ok\n");
}

// ---- TestUnseenThrottle2 ([UnseenThrottle] phase 2, docs/143 §8): rate stepping math and the visible switch, avoidance,
// footprint hold-back ----
static int g_ut2Runs;
static float g_ut2Dt, g_ut2Sum, g_ut2Max;
static int g_ut2Cls;
static void __fastcall Ut2FakeAgent(void*, void*, float dt) {
    g_ut2Runs++, g_ut2Dt = dt, g_ut2Cls = t_unCls;
    g_ut2Sum += dt > UT_MIN_STEP ? dt : UT_MIN_STEP;  // the time behaviour B moves the Sim by (its own clamp)
    if (dt > g_ut2Max) g_ut2Max = dt;
}
static int g_ut2Av;
static void __fastcall Ut2FakeAv(void*, void*, float) { g_ut2Av++; }
struct Ut2FpObj { DWORD vt, pad, idLo, idHi; UnFakeObj* body; };
static void* __fastcall Ut2FpComp(void* obj, void*, DWORD bit) {
    UnFakeObj* b = ((Ut2FpObj*)obj)->body;
    return bit == kUnBitT ? (void*)b : bit == kUnBitS && b->sim ? (void*)b : NULL;
}
static int g_ut2Fp;
static void* g_ut2FpObj;
static DWORD g_ut2FpEn, g_ut2FpForce, g_ut2FpArg;
static void __fastcall Ut2FakeFp(void*, void*, void* obj, DWORD en, DWORD force, DWORD arg) {
    g_ut2Fp++, g_ut2FpObj = obj, g_ut2FpEn = en, g_ut2FpForce = force, g_ut2FpArg = arg;
}
static int g_ut2Svc;
static Ut2FpObj* g_ut2Live;
static void* __cdecl Ut2FakeSvc() { return &g_ut2Svc; }
static u8 __fastcall Ut2FakeFind(void*, void*, DWORD lo, DWORD hi, void** out) {
    if (!g_ut2Live || lo != g_ut2Live->idLo || hi != g_ut2Live->idHi) return 0;
    *out = g_ut2Live;
    return 1;
}
static void __fastcall Ut2FakeRelease(void** pp, void*) { *pp = NULL; }

static void TestUnseenThrottle2() {
    Ts3nCounters& k = ts3native_counters;
    static_assert(offsetof(Ts3nCounters, utAvMode) == 0x3424 && offsetof(Ts3nCounters, utFpMode) == 0x3428 &&
                  offsetof(Ts3nCounters, utRateMode) == 0x342C && offsetof(Ts3nCounters, ut2Ready) == 0x3430 &&
                  offsetof(Ts3nCounters, utAvWhy) == 0x343C && offsetof(Ts3nCounters, utFpCalls) == 0x3484 &&
                  offsetof(Ts3nCounters, utFpWhy) == 0x3498 && offsetof(Ts3nCounters, utFpFlushUs) == 0x34C8 &&
                  offsetof(Ts3nCounters, utRateCalls) == 0x34D0 && offsetof(Ts3nCounters, utRateWhy) == 0x34D8 &&
                  offsetof(Ts3nCounters, utRateCatchUp) == 0x3520 && offsetof(Ts3nCounters, utRateDropped) == 0x3528 &&
                  offsetof(Ts3nCounters, utSnap) == 0x352C && offsetof(Ts3nCounters, utSnapMaxMs) == 0x353C &&
                  sizeof(Ts3nCounters) == 6116 * 4 && UTW_N2 <= 18 && UTF_N <= 12, "counters v80 layout after v79 (docs/registry.md)");
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    // fakes as TestUnseen and TestUnseenThrottle
    const UnAgentFn agentSave = g_unAgentOrig;
    const UnCompFn compSave = g_unComp;
    const UnPosFn posSave = g_unPos;
    const UnFrustumFn frSave = g_unFrustum;
    const UnInFrustumFn inSave = g_unInFrustum;
    const UnCamFn cpSave = g_unCamPos, ctSave = g_unCamTgt;
    const DWORD *btSave = g_unBitTransform, *bsSave = g_unBitSim;
    u8* const* sysSave = g_unLocoSys;
    const UtRemainFn remSave = g_utRemain;
    const UtAvFn avSave = g_utAvOrig;
    const SvcFn svcSave = g_objSvc;
    const FindRefFn findSave = g_findRef;
    const ReleaseFn relSave = g_objRelease;
    void* const lockSave = g_rgLock;
    const DWORD tidSave = g_unSimTid;
    const LONG everySave = k.utEvery, passSave = k.unPasses, ownerSave = k.rgOwnerTid;
    const bool onSave = g_utOn, needSave = g_utNeedViewers, rateSave = g_utRateOn, fpSave = g_utFpOn;
    g_unAgentOrig = Ut2FakeAgent, g_unComp = UnFakeComp, g_unPos = UnFakePos, g_unFrustum = UnFakeFrustum, g_unInFrustum = UnFakeIn;
    g_unCamPos = UnFakeCamPos, g_unCamTgt = UnFakeCamTgt, g_unBitTransform = &kUnBitT, g_unBitSim = &kUnBitS;
    g_utRemain = UtFakeRemain, g_utAvOrig = Ut2FakeAv;
    g_utFRest = 100, g_utFLen = 50, g_utFProg = 20, g_utFFault = false;
    g_unFakeCam[0][0] = 0, g_unFakeCam[0][1] = 30, g_unFakeCam[0][2] = 0;
    memset(g_unFakeCam[1], 0, 12);
    UnRefreshCamera();
    k.utEvery = 4, g_utNeedViewers = true, g_utOn = true;
    const float far1[1][3] = {{1000, 0, 1000}};
    UtPush(far1, 1, 1);  // a fresh household table, far away
    static void* pathVt[0x130 / 4];
    static void* partVt[8];
    pathVt[0x12C / 4] = (void*)UtFakeLen, pathVt[0xA4 / 4] = (void*)UtFakeProg, partVt[0x10 / 4] = (void*)UtFakeCur;
    static void* path[2] = {pathVt, (void*)1}, *path2[2] = {pathVt, (void*)1};  // +4 = refcount, the caller's ref
    __declspec(align(16)) static UnFakeObj sim = {{300, 0, 0, 1}, true}, car = {{300, 0, 0, 1}, false};
    static u8 agent[0x110], sys[0x50];
    *(void***)(agent + 8) = partVt, *(void**)(agent + 0x10) = path;  // part vtable; part+8 = the route
    *(void**)(agent + 4) = &sim, *(DWORD*)(agent + 0x94) = 1;  // owner id = [owner+8] = sim.pos[2] bits = 0
    *(u8**)(sys + 0x40) = agent + 0x40;  // the agent is the list head: every call is a new pass
    static u8* sysPtr = sys;
    g_unLocoSys = &sysPtr;
    memset(g_utRate, 0, sizeof(g_utRate));
    static Ts3nCounters b;
    memcpy(&b, &k, sizeof(k));

    // 1. rate: one run in 4 passes once settled; every pass's step is accounted for (sum passed + debt == sum of
    //    max(dt, 1/30)), no step over 0.5 s, the run lands on the phase-1 turn ((pass + owner id) % 4 == 0)
    k.utRateMode = 1, g_utRateOn = true;
    UtRateSlot& rs = g_utRate[((uintptr_t)agent >> 4) & 511];
    auto runFor = [&](int passes, float dt, float* owed) {  // returns runs; *owed += what behaviour B would have used
        const int r0 = g_ut2Runs;
        for (int i = 0; i < passes; i++) {
            UnAgentThunk(agent, NULL, dt);
            *owed += dt > UT_MIN_STEP ? dt : UT_MIN_STEP;
        }
        return g_ut2Runs - r0;
    };
    float owed = 0;
    g_ut2Sum = 0;
    runFor(2, 0.04f, &owed);  // new path, then the first call after it
    const int runs = runFor(16, 0.04f, &owed);
    assert(runs == 4 && k.utRateSkipped - b.utRateSkipped == 12);
    assert(fabsf(g_ut2Sum + rs.debt - owed) < 1e-4f);  // no time lost or added
    assert(((DWORD)k.unPasses) % 4 != 0 || rs.debt == 0);  // owner id 0: the run is on passes where pass % 4 == 0
    // dt under behaviour B's 1/30 clamp: the debt counts 1/30 per pass, as B would have moved
    g_ut2Sum = 0, owed = rs.debt;
    runFor(12, 0.02f, &owed);
    assert(fabsf(g_ut2Sum + rs.debt - owed) < 1e-4f);
    // big and jumping passes: no update gets more than 0.5 s (the cap runs first, a spike runs the debt on its own), and
    // the time still adds up
    g_ut2Sum = 0, g_ut2Max = 0, owed = rs.debt;
    for (int i = 0; i < 400; i++) runFor(1, 0.01f + 0.2875f * (float)((i * 7919) % 100) / 100.0f, &owed);
    assert(g_ut2Max <= UT_MAX_STEP + 1e-6f && fabsf(g_ut2Sum + rs.debt - owed) < 1e-3f);
    assert(k.utRateWhy[UTW_CAP] > b.utRateWhy[UTW_CAP]);

    // 2. the visible switch: a skipped (owing) Sim that comes into view runs that same pass with dt + its debt, classed visible
    g_ut2Sum = 0, owed = 0;
    while (true) {  // run until it owes something
        UnAgentThunk(agent, NULL, 0.04f);
        if (rs.debt > 0) break;
    }
    const float debt = rs.debt;
    // snap-back watch on this Sim (unseen_snapback's channel): owner id = [owner+8], [owner+0xC]
    DWORD wlo, whi;
    memcpy(&wlo, &sim.pos[2], 4), memcpy(&whi, &sim.pos[3], 4);
    assert(UtMsg(6, wlo) && UtMsg(7, whi) && UtMsg(8, 0) && !UtMsg(8, 1));
    const LONG snap0 = k.utSnap[3];
    sim.pos[0] = 3;  // on screen
    int r0 = g_ut2Runs;
    UnAgentThunk(agent, NULL, 0.04f);
    assert(g_ut2Runs == r0 + 1 && fabsf(g_ut2Dt - (0.04f + debt)) < 1e-6f && g_ut2Cls == 1 && rs.debt == 0);
    // counted as a visible snap-back with its owed time; the watch saw the flip and the update in that same pass
    assert(k.utSnap[3] == snap0 + 1 && k.utSnapMaxMs[3] >= (LONG)(debt * 1000.0f) - 1);
    assert(UtMsg(8, 1) && UtMsg(8, 2) && UtMsg(8, 7) && UtMsg(9, (DWORD)(debt * 1000.0f) - 1) && !UtMsg(9, 1000) && !UtMsg(10, 1) && !UtMsg(16, 1) && !UtMsg(17, 1));
    assert(UtMsg(18, (DWORD)(debt * 1000.0f) - 1) && !UtMsg(18, 1000));  // the debt it owed at its last update
    assert(UtMsg(11, 0) && !UtMsg(8, 0));  // disarmed
    for (int i = 0; i < 6; i++) {  // visible: every pass, plain dt
        r0 = g_ut2Runs;
        UnAgentThunk(agent, NULL, 0.04f);
        assert(g_ut2Runs == r0 + 1 && g_ut2Dt == 0.04f);
    }
    sim.pos[0] = 300;
    UnAgentThunk(agent, NULL, 0.04f);  // first pass after the hold runs
    assert(g_ut2Dt == 0.04f);
    // the camera leaves the Sim after its flip: its later skips count as "unseen again", never as visible skips
    assert(UtMsg(6, wlo) && UtMsg(7, whi));
    sim.pos[0] = 3;
    UnAgentThunk(agent, NULL, 0.04f);
    sim.pos[0] = 300;
    for (int i = 0; i < 8; i++) UnAgentThunk(agent, NULL, 0.04f);
    assert(UtMsg(8, 1) && !UtMsg(10, 1) && UtMsg(16, 1) && UtMsg(17, 1) && UtMsg(11, 0));
    // 3. snap-back rows: each makes an owing Sim's pass run with its debt (prep: owes, and the next pass is not its turn)
    auto prep = [&]() {
        for (int i = 0; i < 16 && (rs.debt == 0 || ((DWORD)k.unPasses + 1) % 4 == 0); i++) UnAgentThunk(agent, NULL, 0.04f);
        assert(rs.debt > 0 && ((DWORD)k.unPasses + 1) % 4 != 0);
    };
    auto expectRun = [&](int why) {
        const LONG n = k.utRateWhy[why];
        const float owe = rs.debt;
        r0 = g_ut2Runs;
        UnAgentThunk(agent, NULL, 0.04f);
        assert(g_ut2Runs == r0 + 1 && k.utRateWhy[why] == n + 1 && rs.debt == 0 && fabsf(g_ut2Dt - (0.04f + owe)) < 1e-6f);
    };
    prep(), agent[0xC8] = 1, expectRun(UTW_EAON), agent[0xC8] = 0;
    prep(), *(DWORD*)(agent + 0x94) = 4, expectRun(UTW_STATE), *(DWORD*)(agent + 0x94) = 1;
    prep(), *(LONG*)(agent + 8 + 0xC) = 1, agent[8 + 0x20] = 1, expectRun(UTW_LINKED);
    *(LONG*)(agent + 8 + 0xC) = 0, agent[8 + 0x20] = 0;
    prep(), *(float*)(agent + 0xE0) = 0.5f, expectRun(UTW_TIMER);
    *(float*)(agent + 0xE0) = 5.0f;  // a long period: no reason (it fires at most once per step anyway)
    prep();
    *(float*)(agent + 0xE0) = 0;
    prep(), g_utFRest = 2, expectRun(UTW_NEAR), g_utFRest = 100;
    prep(), g_utFProg = 1, expectRun(UTW_PORTAL), g_utFProg = 20;
    prep(), *(void**)(agent + 0x10) = path2, expectRun(UTW_NEWPATH);
    prep(), *(void**)(agent + 4) = &car, expectRun(UTW_KIND), *(void**)(agent + 4) = &sim;  // cars: every pass
    // an AnimatedBoat agent is state 1 like a walking Sim but not a Sim: every pass too (car above has state 1 as well)
    static int vehicleParent;
    prep(), sim.parent = &vehicleParent, expectRun(UTW_LINKED), sim.parent = NULL;  // a rider (parented): every pass
    {  // a ridden / (dis)mounting horse pushed by ts3fixes (codes 12-15): every pass like a rider; an empty table lets it skip again
        DWORD hlo, hhi;
        memcpy(&hlo, &sim.pos[2], 4), memcpy(&hhi, &sim.pos[3], 4);
        prep();
        assert(UtMsg(12, 0) && UtMsg(13, hlo) && UtMsg(14, hhi) && UtMsg(15, 1) && g_utHeld.n == 1);
        assert(!UtMsg(13, 0) && UtMsg(12, 0) && !UtMsg(15, 3));  // out of order / count mismatch refused, table kept
        expectRun(UTW_LINKED);
        assert(UtMsg(12, 0) && UtMsg(15, 0) && g_utHeld.n == 0);
        g_utHeld.at = 0;
    }
    // a debt left over a pass without a call is dropped (the agent left the list meanwhile)
    prep();
    k.unPasses += 3;  // UtRateStep directly: no new pass from the thunk
    {
        float st, pre = 0;
        const LONG n = k.utRateDropped;
        UtRateStep(agent, 1, 0, sim.pos, 0.04f, &st, &pre);
        assert(k.utRateDropped == n + 1 && pre == 0);
    }
    // mode off: every pass; the first carries what is still owed, then plain dt
    k.utRateMode = 0;
    UnAgentThunk(agent, NULL, 0.04f);
    assert(rs.debt == 0);
    for (int i = 0; i < 4; i++) { r0 = g_ut2Runs; UnAgentThunk(agent, NULL, 0.04f); assert(g_ut2Runs == r0 + 1 && g_ut2Dt == 0.04f); }
    k.utRateMode = 1;
    assert(k.utRateCalls - b.utRateCalls >= k.utRateSkipped - b.utRateSkipped);
    g_utRateOn = false;

    // 4. avoidance: inside an unseen agent update, one run in 4 on the phase-1 turn; visible, not its own behaviour, near
    //    the goal (3 m) run; 10 m from the goal (phase 1 would run under 15 m) skips
    static u8 beh[0x10];
    *(u8**)(beh + 0xC) = agent + 8;
    memset(g_utAvPrev, 0, sizeof(g_utAvPrev));
    k.utAvMode = 1;
    t_unAgent = agent, t_unCls = 2;
    memset(t_unPos, 0, sizeof(t_unPos));
    UtAvHook(beh, NULL, 0.04f);  // not seen before: runs
    int mask = 0, av0 = g_ut2Av;
    for (int pass = 1; pass <= 8; pass++) {
        k.unPasses = pass;
        const int a0 = g_ut2Av;
        UtAvHook(beh, NULL, 0.04f);
        mask |= (g_ut2Av != a0) << pass;
    }
    assert(mask == ((1 << 4) | (1 << 8)) && g_ut2Av - av0 == 2);  // owner id 0: passes 4 and 8
    k.unPasses = 1;
    g_utFRest = 10;
    { const int a0 = g_ut2Av; UtAvHook(beh, NULL, 0.04f); assert(g_ut2Av == a0); }  // 10 m from the goal: skipped
    t_unCls = 1;  // comes into view after that skip: runs, counted as a snap-back
    { const LONG n = k.utSnap[1]; UtAvHook(beh, NULL, 0.04f); assert(k.utSnap[1] == n + 1); }
    t_unCls = 2;
    UtAvHook(beh, NULL, 0.04f);  // the run after the hold
    g_utFRest = 2;
    { const LONG n = k.utAvWhy[UTW_NEAR]; UtAvHook(beh, NULL, 0.04f); assert(k.utAvWhy[UTW_NEAR] == n + 1); }
    g_utFRest = 100;
    UtAvHook(beh, NULL, 0.04f);  // the run after the hold
    t_unCls = 1;
    { const LONG n = k.utAvWhy[UTW_VISIBLE]; UtAvHook(beh, NULL, 0.04f); assert(k.utAvWhy[UTW_VISIBLE] == n + 1); }
    t_unCls = 2;
    UtAvHook(beh, NULL, 0.04f);
    *(u8**)(beh + 0xC) = agent;  // a linked agent's behaviour
    { const LONG n = k.utAvWhy[UTW_LINKED]; UtAvHook(beh, NULL, 0.04f); assert(k.utAvWhy[UTW_LINKED] == n + 1); }
    *(u8**)(beh + 0xC) = agent + 8;
    t_unKind = 1;  // an AnimatedBoat's avoidance (state 1, not a Sim)
    { const LONG n = k.utAvWhy[UTW_KIND]; UtAvHook(beh, NULL, 0.04f); assert(k.utAvWhy[UTW_KIND] == n + 1); }
    t_unKind = 0, t_unRider = true;  // a rider
    { const LONG n = k.utAvWhy[UTW_LINKED]; UtAvHook(beh, NULL, 0.04f); assert(k.utAvWhy[UTW_LINKED] == n + 1); }
    t_unRider = false;
    UtAvHook(beh, NULL, 0.04f);
    *(LONG*)(agent + 8 + 0xC) = 1, agent[8 + 0x20] = 1;  // a group link (0x85D980 calls into the first linked agent)
    { const LONG n = k.utAvWhy[UTW_LINKED]; UtAvHook(beh, NULL, 0.04f); assert(k.utAvWhy[UTW_LINKED] == n + 1); }
    *(LONG*)(agent + 8 + 0xC) = 0, agent[8 + 0x20] = 0;
    UtAvHook(beh, NULL, 0.04f);
    *(u8**)(beh + 0xC) = agent + 8;
    t_unAgent = NULL, t_unCls = 0;
    { const LONG n = k.utAvWhy[UTW_NOAGENT]; UtAvHook(beh, NULL, 0.04f); assert(k.utAvWhy[UTW_NOAGENT] == n + 1); }
    k.utAvMode = 0;
    { const int a0 = g_ut2Av; UtAvHook(beh, NULL, 0.04f); assert(g_ut2Av == a0 + 1); }
    assert(k.utAvCalls - b.utAvCalls == (LONG)(g_ut2Av - av0 + 1) + k.utAvSkipped - b.utAvSkipped);

    // 5. footprint: an unseen Sim's enable calls within FpMs fold into one pending update, applied once when due with the
    //    last call's arguments (force or-ed); any other call drops it and runs; gone objects are dropped at their due time
    static u8 world[0x1000];
    g_rgLock = world + 0x918;
    g_unSimTid = GetCurrentThreadId();
    g_objSvc = Ut2FakeSvc, g_findRef = Ut2FakeFind, g_objRelease = Ut2FakeRelease;
    g_utFpNext = Ut2FakeFp, g_utFpOn = true, g_utFpMs = 500, g_utFpN = 0;
    memset(g_utFpLast, 0, sizeof(g_utFpLast));
    k.utFpMode = 1, k.rgOwnerTid = 0;
    __declspec(align(16)) static UnFakeObj body = {{300, 0, 0, 1}, true};
    static Ut2FpObj o1 = {0, 0, 0x55, 7, &body};
    g_unComp = Ut2FpComp;
    g_ut2Live = &o1;
    int f0 = g_ut2Fp;
    UtFpHook(world, NULL, &o1, 1, 0, 0);  // first: due (nothing applied yet)
    assert(g_ut2Fp == f0 + 1 && k.utFpWhy[UTF_DUE] == b.utFpWhy[UTF_DUE] + 1);
    UtFpHook(world, NULL, &o1, 1, 0, 0);  // within 500 ms: held back
    UtFpHook(world, NULL, &o1, 0x101, 1, 0);  // the same update (enable byte 1), forced: folds in
    assert(g_ut2Fp == f0 + 1 && g_utFpN == 1 && g_utFpPend[0].force == 1 && k.utFpSkipped - b.utFpSkipped == 2);
    UtFpFlush(true);  // not due yet
    assert(g_ut2Fp == f0 + 1 && g_utFpN == 1);
    g_utFpPend[0].due = GetTickCount() - 1;
    UtFpFlush(true);
    assert(g_ut2Fp == f0 + 2 && g_ut2FpObj == &o1 && g_ut2FpEn == 1 && g_ut2FpForce == 1 && g_utFpN == 0 &&
           k.utFpFlushed == b.utFpFlushed + 1);
    // a disable while one is pending: the pending one is dropped, the disable runs now
    UtFpHook(world, NULL, &o1, 1, 0, 0);
    assert(g_utFpN == 1);
    UtFpHook(world, NULL, &o1, 0, 0, 0);
    assert(g_utFpN == 0 && g_ut2Fp == f0 + 3 && g_ut2FpEn == 0 && k.utFpWhy[UTF_CHANGE] == b.utFpWhy[UTF_CHANGE] + 1);
    // other arguments: the same
    UtFpHook(world, NULL, &o1, 1, 0, 0), UtFpHook(world, NULL, &o1, 1, 0, 0);  // the disable ran 0 ms ago: held, folded
    assert(g_utFpN == 1);
    UtFpHook(world, NULL, &o1, 1, 0, 9);
    assert(g_utFpN == 0 && g_ut2FpArg == 9);
    // the object comes into view: runs, the pending one dropped
    UtFpHook(world, NULL, &o1, 1, 0, 9);
    assert(g_utFpN == 1);
    body.pos[0] = 3;
    UtFpHook(world, NULL, &o1, 1, 0, 9);
    assert(g_utFpN == 0 && k.utFpWhy[UTF_VISIBLE] == b.utFpWhy[UTF_VISIBLE] + 1 && k.utSnap[2] == b.utSnap[2] + 1);  // a visible snap-back
    body.pos[0] = 300;
    // nested under +0x918 (as design U's drain): passes through and drops the pending one
    UtFpHook(world, NULL, &o1, 1, 0, 9);
    assert(g_utFpN == 1);
    k.rgOwnerTid = (LONG)GetCurrentThreadId();
    f0 = g_ut2Fp;
    UtFpHook(world, NULL, &o1, 1, 0, 9);
    assert(g_utFpN == 0 && g_ut2Fp == f0 + 1 && k.utFpWhy[UTF_PASS] == b.utFpWhy[UTF_PASS] + 1);
    k.rgOwnerTid = 0;
    // gone at its due time: dropped, never applied
    UtFpHook(world, NULL, &o1, 1, 0, 9);
    assert(g_utFpN == 1);
    g_ut2Live = NULL;
    g_utFpPend[0].due = GetTickCount() - 1;
    f0 = g_ut2Fp;
    UtFpFlush(true);
    assert(g_utFpN == 0 && g_ut2Fp == f0 && k.utFpGone == b.utFpGone + 1);
    g_ut2Live = &o1;
    // a car, a stale household table, mode off: run
    body.sim = false;
    { const LONG n = k.utFpWhy[UTF_KIND]; UtFpHook(world, NULL, &o1, 1, 0, 9); assert(k.utFpWhy[UTF_KIND] == n + 1); }
    body.sim = true;
    static int seat;
    body.parent = &seat;  // in a vehicle / on a horse: every call
    { const LONG n = k.utFpWhy[UTF_RIDER]; UtFpHook(world, NULL, &o1, 1, 0, 9); assert(k.utFpWhy[UTF_RIDER] == n + 1); }
    body.parent = NULL;
    g_utView.at = GetTickCount() - 5000;
    { const LONG n = k.utFpWhy[UTF_NOVIEW]; UtFpHook(world, NULL, &o1, 1, 0, 9); assert(k.utFpWhy[UTF_NOVIEW] == n + 1); }
    UtPush(far1, 1, 1);
    k.utFpMode = 0;
    { const LONG n = k.utFpWhy[UTF_OFF]; UtFpHook(world, NULL, &o1, 1, 0, 9); assert(k.utFpWhy[UTF_OFF] == n + 1); }
    k.utFpMode = 1;
    // another world's record (travel): dropped at its due time
    UtFpHook(world, NULL, &o1, 1, 0, 9);
    UtFpHook(world, NULL, &o1, 1, 0, 9);
    if (g_utFpN) {
        g_rgLock = world + 0x100;
        g_utFpPend[0].due = GetTickCount() - 1;
        const LONG n = k.utFpGone;
        UtFpFlush(true);
        assert(g_utFpN == 0 && k.utFpGone == n + 1);
        g_rgLock = world + 0x918;
    }

    assert(path[1] == (void*)1 && path2[1] == (void*)1);  // path references balanced
    g_unAgentOrig = agentSave, g_unComp = compSave, g_unPos = posSave, g_unFrustum = frSave, g_unInFrustum = inSave;
    g_unCamPos = cpSave, g_unCamTgt = ctSave, g_unBitTransform = btSave, g_unBitSim = bsSave, g_unLocoSys = sysSave;
    g_utRemain = remSave, g_utAvOrig = avSave, g_objSvc = svcSave, g_findRef = findSave, g_objRelease = relSave;
    g_rgLock = lockSave, g_unSimTid = tidSave;
    k.utEvery = everySave, k.unPasses = passSave, k.rgOwnerTid = ownerSave;
    k.utAvMode = k.utFpMode = k.utRateMode = 0;
    g_utOn = onSave, g_utNeedViewers = needSave, g_utRateOn = rateSave, g_utFpOn = fpSave, g_utFpNext = NULL, g_utFpN = 0;
    g_utView.at = 0, g_utView.n = 0, g_utInOpen = false;
    g_unCam.ok = false, g_unCam.at.QuadPart = 0;
    memset(g_utRate, 0, sizeof(g_utRate)), memset(g_utAvPrev, 0, sizeof(g_utAvPrev)), memset(g_unLast, 0, sizeof(g_unLast));
    memset(g_utFpLast, 0, sizeof(g_utFpLast));
    printf("UnseenThrottle phase 2: rate (1 run in 4, summed step = sum of max(dt, 1/30), cap 0.5 s, visible switch runs the "
           "same pass with its debt, snap-back rows, dropped debt), avoidance (turn, 3 m goal, visible, linked), footprint "
           "(hold-back, fold, flush when due, disable / other args / visible / nested drop it, gone, other world): ok\n");
    memset(g_utDg, 0, sizeof(g_utDg));  // footprint holds above recorded skips
}

// ---- TestUnseenDiag ([UnseenThrottle] diagnostics, docs/143 §8.14, counters v83): replan split by follower, last skipping
// part and its age, deferred notices ----
static u8* __cdecl UtFakeRp(u8* out, DWORD) { return out; }
static u8* __cdecl UtFakeRpPos(u8* out, DWORD, float, float, float) { return out; }
static void TestUnseenDiag() {
    Ts3nCounters& k = ts3native_counters;
    static_assert(offsetof(Ts3nCounters, utDgReady) == 0x3568 && offsetof(Ts3nCounters, utDefer) == 0x356C &&
                  offsetof(Ts3nCounters, utListNotices) == 0x3578 && offsetof(Ts3nCounters, utRpN) == 0x357C &&
                  offsetof(Ts3nCounters, utRpUs) == 0x3594 && offsetof(Ts3nCounters, utRpLong) == 0x35AC &&
                  offsetof(Ts3nCounters, utRpPart) == 0x35C4 && offsetof(Ts3nCounters, utRpPartMs) == 0x35E4 &&
                  sizeof(Ts3nCounters) == 6116 * 4, "counters v83 layout after v81 (docs/registry.md)");
    const UnCompFn compSave = g_unComp;
    const UnPosFn posSave = g_unPos;
    const UnFrustumFn frSave = g_unFrustum;
    const UnInFrustumFn inSave = g_unInFrustum;
    const UnFindFn findSave = g_unFind;
    const AsRouteFn routeSave = g_unRoute;
    const DWORD *btSave = g_unBitTransform, *bsSave = g_unBitSim;
    void* const* mgrSave = g_unObjMgr;
    const u8* const* rmSave = g_unRmSlot;
    const DWORD tidSave = g_unSimTid;
    const LONG passSave = k.unPasses;
    g_unComp = UnFakeComp, g_unPos = UnFakePos, g_unFrustum = UnFakeFrustum, g_unInFrustum = UnFakeIn, g_unFind = UnFakeFind;
    g_unRoute = UnFakeRouteLookup, g_unBitTransform = &kUnBitT, g_unBitSim = &kUnBitS;
    static const u8 rmDummy = 0;
    static const u8* rmPtr = &rmDummy;
    static void* mgrPtr = (void*)&rmDummy;
    g_unRmSlot = &rmPtr, g_unObjMgr = &mgrPtr;
    memset(g_unFakeRoute, 0, sizeof(g_unFakeRoute));
    *(DWORD*)(g_unFakeRoute + 0x40) = 0x1234, *(DWORD*)(g_unFakeRoute + 0x44) = 5;  // follower id: UnFakeFind -> sim
    __declspec(align(16)) static UnFakeObj sim = {{300, 0, 0, 1}, true};
    g_unFakeFound = &sim;
    g_unSimTid = GetCurrentThreadId();
    g_utRpOrig = UtFakeRp, g_utRpPosOrig = UtFakeRpPos;
    memset(g_utDg, 0, sizeof(g_utDg));
    k.unPasses = 5000;
    static Ts3nCounters b;
    memcpy(&b, &k, sizeof(k));
    u8 out[0x48];
    // 1. never skipped: "other follower"; an unknown route: "no follower"
    assert(UtRpHook(out, 7) == out && k.utRpN[0][1] == b.utRpN[0][1] + 1);
    UtRpHook(out, 8);
    assert(k.utRpN[0][2] == b.utRpN[0][2] + 1);
    // 2. a rate skip, then a ReplanFromPosition: throttled lately, rate was last
    UtDgSkip(UT_DG_RATE, &sim);
    assert(UtRpPosHook(out, 7, 1, 2, 3) == out && k.utRpN[1][0] == b.utRpN[1][0] + 1);
    assert(k.utRpPart[1][UT_DG_RATE] == b.utRpPart[1][UT_DG_RATE] + 1);
    // 3. a later footprint skip wins the attribution, with its age
    UtDgSlot* s = UtDgFor(&sim, false);
    assert(s && s->at[UT_DG_RATE]);
    s->at[UT_DG_RATE] -= 400;
    UtDgSkip(UT_DG_FOOT, &sim);
    s->at[UT_DG_FOOT] -= 50;
    UtRpHook(out, 7);
    assert(k.utRpN[0][0] == b.utRpN[0][0] + 1 && k.utRpPart[0][UT_DG_FOOT] == b.utRpPart[0][UT_DG_FOOT] + 1);
    const LONG ageMs = k.utRpPartMs[0][UT_DG_FOOT] - b.utRpPartMs[0][UT_DG_FOOT];
    assert(ageMs >= 49);  // the stamp is GetTickCount() | 1
    TIMING_ASSERT("UnseenProbe part age < 399 ms", ageMs < 399);
    // 4. more than 8 passes since the last skip: "other" again
    k.unPasses += UT_DG_RECENT + 1;
    UtRpHook(out, 7);
    assert(k.utRpN[0][1] == b.utRpN[0][1] + 2);
    // 5. deferred notices: list / avoidance / rate skips after a non-empty list count, footprint skips never do
    UtDgSkip(UT_DG_LIST, &sim);
    assert(k.utDefer[0] == b.utDefer[0]);  // its last list was empty
    s->obst = true;
    UtDgSkip(UT_DG_LIST, &sim), UtDgSkip(UT_DG_AVOID, &sim), UtDgSkip(UT_DG_RATE, &sim), UtDgSkip(UT_DG_FOOT, &sim);
    assert(k.utDefer[0] == b.utDefer[0] + 1 && k.utDefer[1] == b.utDefer[1] + 1 && k.utDefer[2] == b.utDefer[2] + 1);
    // 6. a call from another thread is passed through, not counted
    g_unSimTid = 0;
    UtRpHook(out, 7);
    assert(k.utRpN[0][0] + k.utRpN[0][1] + k.utRpN[0][2] == b.utRpN[0][0] + b.utRpN[0][1] + b.utRpN[0][2] + 4);
    assert(k.utRpLong[0][0] == b.utRpLong[0][0]);
    // 7. per-Sim skip read (codes 19-23, unseen_water_cause): found by object id, newest part and its age, side switch
    static DWORD idObj[4] = {0, 0, 0xAB, 0xCD};
    assert(UtSkipQuery(19, 0xAB) && !UtSkipQuery(20, 0xCE) && !UtSkipQuery(21, 100000));  // unknown id
    UtDgSkip(UT_DG_AVOID, idObj);
    UtDgFor(idObj, false)->at[UT_DG_AVOID] -= 300;
    UtDgSkip(UT_DG_RATE, idObj);
    UtDgFor(idObj, false)->at[UT_DG_RATE] -= 100;
    assert(UtSkipQuery(19, 0xAB) && UtSkipQuery(20, 0xCD));
    assert(UtSkipQuery(22, UT_DG_RATE) && !UtSkipQuery(22, UT_DG_AVOID) && UtSkipQuery(21, 250) && !UtSkipQuery(21, 50));
    const LONG modeSave = k.utMode, avSave = k.utAvMode, rateSave = k.utRateMode, fpSave = k.utFpMode;
    k.utMode = k.utAvMode = k.utRateMode = k.utFpMode = 0;
    assert(!UtSkipQuery(23, 0));
    k.utRateMode = 1;
    assert(UtSkipQuery(23, 0));
    k.utMode = modeSave, k.utAvMode = avSave, k.utRateMode = rateSave, k.utFpMode = fpSave;

    g_unComp = compSave, g_unPos = posSave, g_unFrustum = frSave, g_unInFrustum = inSave, g_unFind = findSave, g_unRoute = routeSave;
    g_unBitTransform = btSave, g_unBitSim = bsSave, g_unObjMgr = mgrSave, g_unRmSlot = rmSave, g_unSimTid = tidSave;
    g_utRpOrig = NULL, g_utRpPosOrig = NULL, k.unPasses = passSave;
    memset(g_utDg, 0, sizeof(g_utDg));
    printf("UnseenThrottle diagnostics: replan split (throttled lately / other / no follower), last skipping part and its age, "
           "8-pass window, deferred notices (list, avoidance, rate; not footprint): ok\n");
}
