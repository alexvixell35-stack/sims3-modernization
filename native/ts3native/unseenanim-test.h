// TestUnseenAnim ([UnseenAnim], unseenanim.cpp, docs/144 §3.4). Included by selftest.cpp after unseen-test.h (its fakes).
// Fakes only; no game code runs. Checks: objects and visible Sims run every pass; an unseen Sim runs every Every-th pass
// with the summed dt (no time lost); snap-back on visible with the owed time; holds (parented, near a visible Sim, near a
// household viewer, no household table, debt cap); switch off flushes the debt; a reused actor address drops the old debt.
// Objects (TestUnseenAnimObjects): unseen object skips with time kept, snap-back, SACS / agent / no list / parent holds,
// ObjRadiusM sphere, switch-off flush, histogram freeze, off = pass-through.
// UnFakeObj (unseen-test.h): +0 pos, sim flag, +0x30 parent; the owner id at +8/+0xC overlaps pos[2], pos[3]
static float g_aaFakeDt[16];
static int g_aaFakeN;
static void __fastcall AaFakePose(void*, void*, float dt) { if (g_aaFakeN < 16) g_aaFakeDt[g_aaFakeN] = dt; g_aaFakeN++; }
static void __fastcall AaFakePass(void*, void*, float) {}

// Objects (docs/144 §3.6): an owner with SACS bit kAoBitSacs gets a non-empty state-machine vector when it is
// g_aoFakeSacsOwner; the frustum fake here honours the sphere radius (ObjRadiusM test); one fake locomotion agent.
static const DWORD kAoBitSacs = 0x40;
static void* g_aoFakeSacsOwner;
static void* g_aoFakeSacs[4] = {NULL, NULL, &g_aoFakeSacs[0], &g_aoFakeSacs[1]};  // +8 begin != +0xC end
static void* g_aoFakeSacsEmpty[4];
static void* __fastcall AoFakeComp(void* obj, void* edx, DWORD bit) {
    if (bit == kAoBitSacs) return obj == g_aoFakeSacsOwner ? (void*)g_aoFakeSacs : (void*)g_aoFakeSacsEmpty;
    return UnFakeComp(obj, edx, bit);
}
static u8 __fastcall AoFakeIn(const void*, void*, const float* s) { return s[0] - s[3] < 10 && s[0] + s[3] > -10; }

// called from TestUnseenAnim with its fakes in place (camera at (0,30,0), fresh far household table, aaMode 1)
static void TestUnseenAnimObjects() {
    Ts3nCounters& k = ts3native_counters;
    u8* const* sysSave = g_unLocoSys;
    const DWORD* sacsSave = g_aaBitSacs;
    const UnInFrustumFn inSave = g_unInFrustum;
    static u8 aoSys[0x48], aoAgent[0x48];
    static u8* aoSysPtr;
    aoSysPtr = aoSys;
    auto agents = [&](void* owner) {
        if (owner) *(void**)(aoAgent + 4) = owner, *(u8**)(aoAgent + 0x40) = aoSys + 0x40, *(u8**)(aoSys + 0x40) = aoAgent + 0x40;
        else *(u8**)(aoSys + 0x40) = aoSys + 0x40;
    };
    g_unLocoSys = &aoSysPtr, g_aaBitSacs = &kAoBitSacs, g_unComp = AoFakeComp, g_unInFrustum = AoFakeIn;
    agents(NULL);
    memset(g_aaSlot, 0, sizeof g_aaSlot), memset(g_aaVis, 0, sizeof g_aaVis), memset(g_aoVt, 0, sizeof g_aoVt);
    g_aoOn = g_aoStats = true, g_aoEvery = 4, g_aoUseM = 8, g_aoRadius = 5, g_aoFreezeAt = 32, g_aoAsks = g_aoPre = 0;
    g_aaCapPasses = 0, k.aoMode = 1, k.aoFrozen = 0;
    memset(k.aoBktVt, 0, sizeof k.aoBktVt);
    __declspec(align(16)) static UnFakeObj fountain = {{500, 0, 300, 1}, false}, big = {{14, 0, 200, 1}, false},
                                         walker = {{503, 0, 302, 1}, true};
    static u8 aFtn[0x30], aBig[0x30];
    *(void**)(aFtn + 4) = &fountain, *(void**)(aBig + 4) = &big;
    const float dt = 0.02f;
    AaSlot* s = &g_aaSlot[((uintptr_t)aFtn >> 4) & (AA_SLOTS - 1)];
    auto pass = [&](void* actor) { AaPassThunk(NULL, NULL, dt); g_aaFakeN = 0; AaPoseThunk(actor, NULL, dt); return g_aaFakeN; };
    auto runs8 = [&](void* actor) { int r = 0; for (int i = 0; i < 8; i++) r += pass(actor); return r; };
    // an unseen object with nothing near: 2 runs in 8 passes, no time lost
    float ran = 0;
    int runs = 0;
    for (int i = 0; i < 8; i++) { if (pass(aFtn)) runs++, ran += g_aaFakeDt[0]; }
    assert(runs == 2 && k.aoSkipped >= 6 && ran + s->owed > 8 * dt - 1e-5f && ran + s->owed < 8 * dt + 1e-5f);
    // seen again: runs this pass with what it owes
    while (s->owed == 0) pass(aFtn);
    float owed = s->owed;
    const LONG snap0 = k.aoSnap;
    fountain.pos[0] = 5;
    assert(pass(aFtn) == 1 && g_aaFakeDt[0] > owed + dt - 1e-5f && k.aoSnap == snap0 + 1 && s->owed == 0);
    fountain.pos[0] = 500;
    // holds, every pass: in a SACS state machine, a Sim agent within UseM, the owner is an agent, no agent list, parented
    g_aoFakeSacsOwner = &fountain;
    LONG h0 = k.aoHold[AOH_SACS];
    assert(runs8(aFtn) == 8 && k.aoHold[AOH_SACS] == h0 + 8);
    g_aoFakeSacsOwner = NULL;
    agents(&walker);
    h0 = k.aoHold[AOH_AGENT];
    assert(runs8(aFtn) == 8 && k.aoHold[AOH_AGENT] == h0 + 8);
    agents(&fountain);
    assert(runs8(aFtn) == 8 && k.aoHold[AOH_AGENT] == h0 + 16);
    walker.pos[0] = 520;  // 20 m away (outside UseM and the cell window): skips again, counted in aoNear
    agents(&walker);
    LONG n0 = k.aoNear[0] + k.aoNear[1];
    assert(runs8(aFtn) == 2 && k.aoNear[0] + k.aoNear[1] > n0);
    aoSysPtr = NULL;
    const LONG wf0 = k.aoWalkFail;
    assert(runs8(aFtn) == 8 && k.aoWalkFail == wf0 + 8);
    aoSysPtr = aoSys;
    static u8 parent[4];
    fountain.parent = parent;
    h0 = k.aoHold[AOH_PARENT];
    assert(runs8(aFtn) == 8 && k.aoHold[AOH_PARENT] == h0 + 8);
    fountain.parent = NULL;
    // a big model 200 m away whose origin is off screen but whose ObjRadiusM sphere is on screen: seen, never skipped
    const LONG sk0 = k.aoSkipped;
    assert(runs8(aBig) == 8 && k.aoSkipped == sk0);
    // crash triage 2026-11-02: debt of an actor not asked for > 2 x ObjEvery passes is dropped, not paid; a new list clears all
    while (s->owed == 0) pass(aFtn);
    const LONG st0 = k.aaStale, rs0 = k.aaSlotReset;
    for (int i = 0; i < 9; i++) AaPassThunk(NULL, NULL, dt);
    pass(aFtn);
    assert(k.aaStale == st0 + 1 && s->owed < dt + 1e-5f);
    while (s->owed == 0) pass(aFtn);
    static u8 list2[4];
    AaPassThunk(list2, NULL, dt), AaPassThunk(NULL, NULL, dt);  // first list from none is not counted, list2 -> NULL is
    assert(k.aaSlotReset == rs0 + 1 && s->owed == 0 && s->actor == NULL);
    // switch off: the owed time runs at the next call, then every pass
    while (s->owed == 0) pass(aFtn);
    owed = s->owed;
    k.aoMode = 0;
    const LONG off0 = k.aoFlushOff;
    assert(pass(aFtn) == 1 && g_aaFakeDt[0] > owed + dt - 1e-5f && k.aoFlushOff == off0 + 1 && runs8(aFtn) == 8);
    // histogram: buckets fixed after 32 asks (the fake owner's "vtable" is its x float), kinds and buckets sampled
    LONG kn = 0, bn = 0;
    for (int i = 0; i < 10; i++) kn += k.aoKindN[i / 2][i % 2];
    for (int i = 0; i < 9; i++) bn += k.aoBktN[i];
    assert(k.aoFrozen >= 32 && k.aoBktVt[0] != 0 && kn > 0 && bn > 0 && k.aoKindN[AOK_PLAIN][1] > 0);
    // Objects and ObjStats off: plain pass-through
    g_aoOn = g_aoStats = false;
    const LONG skip1 = k.aoSkipped, n2 = k.aaN[2];
    assert(runs8(aFtn) == 8 && k.aoSkipped == skip1 && k.aaN[2] == n2 + 8);

    k.aoMode = 0, g_aoFreezeAt = 65536, g_aoAsks = g_aoPre = 0;
    memset(g_aoVt, 0, sizeof g_aoVt);
    g_unLocoSys = sysSave, g_aaBitSacs = sacsSave, g_unInFrustum = inSave, g_unComp = UnFakeComp;
}

static void TestUnseenAnim() {
    Ts3nCounters& k = ts3native_counters;
    static Ts3nCounters saved;
    memcpy(&saved, &k, sizeof(k));
    const AaPoseFn poseSave = g_aaPoseOrig;
    const AaPassFn passSave = g_aaPassOrig;
    const UnCompFn compSave = g_unComp;
    const UnPosFn posSave = g_unPos;
    const UnFrustumFn frSave = g_unFrustum;
    const UnInFrustumFn inSave = g_unInFrustum;
    const UnCamFn cpSave = g_unCamPos, ctSave = g_unCamTgt;
    const DWORD *btSave = g_unBitTransform, *bsSave = g_unBitSim;
    const bool needSave = g_utNeedViewers;
    const auto viewSave = g_utView;
    g_aaPoseOrig = AaFakePose, g_aaPassOrig = AaFakePass;
    g_unComp = UnFakeComp, g_unPos = UnFakePos, g_unFrustum = UnFakeFrustum, g_unInFrustum = UnFakeIn;
    g_unCamPos = UnFakeCamPos, g_unCamTgt = UnFakeCamTgt, g_unBitTransform = &kUnBitT, g_unBitSim = &kUnBitS;
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    g_unFakeCam[0][0] = 0, g_unFakeCam[0][1] = 30, g_unFakeCam[0][2] = 0;  // reach 80 around the origin, screen |x| < 10
    memset(g_unFakeCam[1], 0, 12);
    memset(g_aaSlot, 0, sizeof g_aaSlot);
    memset(g_aaVis, 0, sizeof g_aaVis);
    g_aaEvery = 4, g_aaMaxOwed = 0.25f, g_aaNearVis2 = 100.0f, g_utNeedViewers = true;
    k.aaMode = 1;
    // a fresh household table far away from everyone (one viewer at x = -1000)
    g_utView.n = 1, g_utView.pos[0][0] = -1000, g_utView.pos[0][1] = 0, g_utView.pos[0][2] = 0, g_utView.at = (GetTickCount() - 1) | 1;

    __declspec(align(16)) static UnFakeObj farSim = {{500, 0, 0, 1}, true}, nearSim = {{2, 0, 0, 1}, true},
                                         chair = {{500, 0, 0, 1}, false};
    static u8 aFar[0x30], aNear[0x30], aChair[0x30];
    *(void**)(aFar + 4) = &farSim, *(void**)(aNear + 4) = &nearSim, *(void**)(aChair + 4) = &chair;
    const float dt = 0.02f;
    auto pass = [&](void* actor) { AaPassThunk(NULL, NULL, dt); g_aaFakeN = 0; AaPoseThunk(actor, NULL, dt); return g_aaFakeN; };
    // other actors and visible Sims: every pass, their own dt
    assert(pass(aChair) == 1 && g_aaFakeDt[0] == dt && pass(aNear) == 1 && g_aaFakeDt[0] == dt);
    // a far unseen Sim: 3 of 4 passes skipped, the 4th carries all the time
    float ran = 0;
    int runs = 0;
    for (int i = 0; i < 8; i++) { if (pass(aFar)) runs++, ran += g_aaFakeDt[0]; }
    assert(runs == 2 && k.aaSkipped >= 6 && k.aaOwedRuns >= 1);  // the first run may come before any skip (ASLR phase)
    const float owedNow = g_aaSlot[((uintptr_t)aFar >> 4) & (AA_SLOTS - 1)].owed;
    assert(ran + owedNow > 8 * dt - 1e-5f && ran + owedNow < 8 * dt + 1e-5f);  // no time lost or added
    // snap-back: the same Sim on screen runs this pass with what it owes
    while (g_aaSlot[((uintptr_t)aFar >> 4) & (AA_SLOTS - 1)].owed == 0) pass(aFar);
    const float owed = g_aaSlot[((uintptr_t)aFar >> 4) & (AA_SLOTS - 1)].owed;
    const LONG snap0 = k.aaSnap;
    const auto watchSave = g_utWatch;  // unseen_snapback code 24: the watched Sim's owed pose time at its first visible call
    memset(&g_utWatch, 0, sizeof g_utWatch);
    memcpy(&g_utWatch.lo, &farSim.pos[2], 4), memcpy(&g_utWatch.hi, &farSim.pos[3], 4), g_utWatch.armed = true;
    farSim.pos[0] = 5;
    assert(pass(aFar) == 1 && g_aaFakeDt[0] > owed + dt - 1e-5f && k.aaSnap == snap0 + 1);
    assert(g_utWatch.animFlipped && g_utWatch.animOwed == owed);
    g_utWatch = watchSave;
    assert(g_aaSlot[((uintptr_t)aFar >> 4) & (AA_SLOTS - 1)].owed == 0);
    farSim.pos[0] = 500;
    // holds: near a Sim visible last pass (8 m), parented, household viewer within 80 m, stale table, debt cap
    __declspec(align(16)) static UnFakeObj buddy = {{12, 0, 92, 1}, true};  // unseen: off screen, ~93 m from camera and target
    static u8 aBuddy[0x30];
    *(void**)(aBuddy + 4) = &buddy;
    __declspec(align(16)) static UnFakeObj visSim = {{8, 0, 90, 1}, true};  // on screen (|x| < 10), 4.5 m from buddy
    static u8 aVis[0x30];
    *(void**)(aVis + 4) = &visSim;
    for (int i = 0; i < 4; i++) {
        AaPassThunk(NULL, NULL, dt);
        AaPoseThunk(aVis, NULL, dt);
        g_aaFakeN = 0;
        AaPoseThunk(aBuddy, NULL, dt);
        assert(i == 0 || g_aaFakeN == 1);  // pass 0: no visible list from the pass before yet
    }
    assert(k.aaHold[AAH_NEARVIS] >= 3);
    static u8 parent[4];
    farSim.parent = parent;
    for (int i = 0; i < 4; i++) assert(pass(aFar) == 1);
    assert(k.aaHold[AAH_PARENT] >= 3);
    farSim.parent = NULL;
    g_utView.pos[0][0] = 450;  // household Sim 50 m away
    for (int i = 0; i < 4; i++) assert(pass(aFar) == 1);
    assert(k.aaHold[AAH_VIEWER] >= 3);
    g_utView.pos[0][0] = -1000, g_utView.at = GetTickCount() - 10000;  // stale table
    for (int i = 0; i < 4; i++) assert(pass(aFar) == 1);
    assert(k.aaHold[AAH_NOVIEW] >= 3);
    g_utView.at = (GetTickCount() - 1) | 1;
    const LONG cap0 = k.aaHold[AAH_CAP];
    for (int i = 0; i < 8; i++) { AaPassThunk(NULL, NULL, 0.1f); g_aaFakeN = 0; AaPoseThunk(aFar, NULL, 0.1f); }  // fast speed
    assert(k.aaHold[AAH_CAP] > cap0 && k.aaDebtMaxUs <= 250000);
    // CapPasses=4 (docs/144 §3.5): a 0.3 s pass (over MaxOwedMs alone) still skips; the step stays <= 4 passes and HardCapMs
    while (g_aaSlot[((uintptr_t)aFar >> 4) & (AA_SLOTS - 1)].owed > 0) pass(aFar);
    g_aaCapPasses = 4, g_aaHardCap = 1.5f;
    const LONG skip0 = k.aaSkipped;
    float maxStep = 0;
    for (int i = 0; i < 8; i++) {
        AaPassThunk(NULL, NULL, 0.3f);
        g_aaFakeN = 0;
        AaPoseThunk(aFar, NULL, 0.3f);
        if (g_aaFakeN && g_aaFakeDt[0] > maxStep) maxStep = g_aaFakeDt[0];
    }
    assert(k.aaSkipped >= skip0 + 5 && maxStep < 4 * 0.3f + 1e-4f && maxStep > 0.3f + 1e-4f);
    assert(AaCap(0.3f) == 1.2f && AaCap(0.01f) == 0.25f && AaCap(1.0f) == 1.5f);
    g_aaCapPasses = 0;
    assert(AaCap(0.3f) == 0.25f);
    // switch off: the owed time runs at the next call, then every pass
    while (g_aaSlot[((uintptr_t)aFar >> 4) & (AA_SLOTS - 1)].owed == 0) pass(aFar);
    k.aaMode = 0;
    const LONG off0 = k.aaFlushOff;
    assert(pass(aFar) == 1 && k.aaFlushOff == off0 + 1 && pass(aFar) == 1 && pass(aFar) == 1);
    k.aaMode = 1;
    // a reused actor address (another Sim id): the old debt is dropped, not run for the new owner
    while (g_aaSlot[((uintptr_t)aFar >> 4) & (AA_SLOTS - 1)].owed == 0) pass(aFar);
    farSim.pos[2] = 7;  // the id low word at owner +8 changes
    AaSlot* s = &g_aaSlot[((uintptr_t)aFar >> 4) & (AA_SLOTS - 1)];
    g_aaFakeN = 0;
    AaPassThunk(NULL, NULL, dt);
    AaPoseThunk(aFar, NULL, dt);
    assert(g_aaFakeN == 0 ? s->owed > dt - 1e-5f && s->owed < dt + 1e-5f : g_aaFakeDt[0] == dt);
    assert(k.aaPasses > 0 && k.aaN[0] > 0 && k.aaN[1] > 0 && k.aaN[2] > 0);
    {  // docs/249: a held Sim through the real channel, UnseenThrottle off: never skipped, each class counted; both id halves must match
        DWORD hlo, hhi;
        const bool onSave = g_utOn, aaSave = g_aaOn;
        const auto heldSave = g_utHeld;
        g_utOn = false, g_aaOn = true;
        memcpy(&hlo, &farSim.pos[2], 4), memcpy(&hhi, &farSim.pos[3], 4);
        const LONG push0 = k.kdAaHeldPush;
        assert(UtMsg(12, 0) && UtMsg(13, hlo) && UtMsg(14, hhi + 1) && UtMsg(15, 1) && k.kdAaHeldPush == push0 + 1);  // same low half only
        LONG sk = k.aaSkipped, in0 = k.kdAaHeldIn;
        for (int i = 0; i < 8; i++) pass(aFar);
        assert(k.aaSkipped > sk && k.kdAaHeldIn == in0);
        assert(UtMsg(12, 0) && UtMsg(13, hlo) && UtMsg(14, hhi) && UtMsg(15, 1) && k.kdAaHeldRows == 1);
        sk = k.aaSkipped;
        const LONG held0 = k.kdAaHeld, seen0 = k.kdAaHeldSeen, par0 = k.kdAaHeldPar;
        for (int i = 0; i < 8; i++) assert(pass(aFar) == 1);
        assert(k.aaSkipped == sk && k.kdAaHeld == held0 + 8 && k.kdAaHeldIn == in0 + 8);
        farSim.pos[0] = 5;  // on screen
        assert(pass(aFar) == 1 && k.kdAaHeldSeen == seen0 + 1 && k.kdAaHeldIn == in0 + 9);
        farSim.pos[0] = 500, farSim.parent = parent;
        assert(pass(aFar) == 1 && k.kdAaHeldPar == par0 + 1 && k.kdAaHeldIn == in0 + 10);
        farSim.parent = NULL;
        g_utHeld.at = GetTickCount() - 10000;  // stale: the table no longer speaks (ts3fixes stopped), the viewer table's own stale hold covers it
        sk = k.aaSkipped;
        for (int i = 0; i < 8; i++) pass(aFar);
        assert(k.aaSkipped > sk && k.kdAaHeldIn == in0 + 10);
        g_utHeld = heldSave, g_utOn = onSave, g_aaOn = aaSave;
    }
    TestUnseenAnimObjects();

    memset(g_aaSlot, 0, sizeof g_aaSlot);
    memset(g_aaVis, 0, sizeof g_aaVis);
    g_utView = viewSave;
    g_aaPoseOrig = poseSave, g_aaPassOrig = passSave;
    g_unComp = compSave, g_unPos = posSave, g_unFrustum = frSave, g_unInFrustum = inSave;
    g_unCamPos = cpSave, g_unCamTgt = ctSave, g_unBitTransform = btSave, g_unBitSim = bsSave;
    g_utNeedViewers = needSave;
    memcpy(&k, &saved, sizeof(k));
    printf("selftest unseen anim ok\n");
}
