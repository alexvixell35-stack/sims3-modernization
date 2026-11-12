// TestUnseenRootMotion ([UnseenRootMotion], unseenrm.cpp, docs/228). Included by selftest.cpp after unseenanim-test.h.
// Fakes only; no game code runs (the naked stubs RmSubStub and RmClipStub are not exercised: the clip stub's effect is
// g_rmDirty = 1, set directly). Fake actor list in EA's layout (next link at entry +0x18, entry = link - 0x18, cursor at
// list +0x10, sentinel = list - 0x18), fake root-motion test (entry +0x2C bit 15 = forced, bit 0 = "a track has root motion",
// needs the rig like 0x582830), fake rig (+0x24, +0xB0 set), fake owner (refcount +4, vtable +0 release), fake head insert
// (0x5718C0). Checks: EA's step count for k ticks; the Mode 1 shortlist walks call the pose in exactly EA's order with EA's
// dts (reference loop below) for an unchanged list, an entry linked after walk 1 (insert hook), an unlink inside a walk, a
// root-motion start by a pose call after the victim's walk-1 test (clip dirty; victim later and earlier in the list), a
// bit-15 start, an unhooked start of an actor in last pass's root-motion set (stepped, no miss: v156 superset, docs/228
// §10.6), and an audit pass that steps an unhooked start and counts it in rmSlMiss, then the 63-pass hold (counted in Mode 1
// passes), a walk-1 flip outside last pass's set found by the rescan before the first sub-step (rmSlW1Late, no miss, also
// in an audit pass: docs/228 §10.7); off and full shortlist hand
// everything to EA; refcount pair and release as EA; rmLateRm; flips; one sampled pass (v153, docs/228 §10).
struct RmFakeOwner { void** vt; LONG ref; };
static LONG g_rmFakeRel;
static void __fastcall RmFakeRelease(void*, void*, int) { g_rmFakeRel++; }
static void* g_rmFakeVt[1] = {(void*)RmFakeRelease};
static bool __fastcall RmFakeTest(void* e) {
    const DWORD f = *(const DWORD*)((const u8*)e + 0x2C);
    const u8* rig = *(const u8* const*)((const u8*)e + 0x24);
    return (f & 0x8000) || ((f & 1) && rig && *(void* const*)(rig + 0xB0));
}
static void __fastcall RmFakeTs(void*, void*, float) {}
static void __fastcall RmFakeIns(void* list, void*, void* e) {  // 0x5718C0: link at the head
    *(u8**)((u8*)e + 0x18) = *(u8**)list, *(u8**)list = (u8*)e + 0x18;
}
static struct { void* a; float dt; } g_rmFakeLog[64];
static int g_rmFakeN;
enum { RMF_UNLINK = 1, RMF_START_DIRTY, RMF_START_SILENT, RMF_START_B15 };
static u8 *g_rmFakeTrigger, *g_rmFakeVictim, *g_rmFakeList, *g_rmFakeRig;  // first pose of trigger acts on victim (once)
static int g_rmFakeAct;
static void RmFakeUnlink(u8* list, u8* v) {
    for (u8* link = list;; ) {  // link = some entry's +0x18 (or the list head)
        u8* nx = *(u8**)link;
        if (nx == v + 0x18) {
            *(u8**)link = *(u8**)(v + 0x18);
            if (*(u8**)(list + 0x10) == v) *(u8**)(list + 0x10) = *(u8**)(v + 0x18) - 0x18;  // EA keeps the cursor valid
            return;
        }
        if (nx == list) return;
        link = nx;
    }
}
static void __fastcall RmFakePose(void* a, void*, float dt) {
    if (g_rmFakeN < 64) g_rmFakeLog[g_rmFakeN].a = a, g_rmFakeLog[g_rmFakeN].dt = dt;
    g_rmFakeN++;
    if (a != g_rmFakeTrigger) return;
    u8* const v = g_rmFakeVictim;
    g_rmFakeTrigger = NULL;
    if (g_rmFakeAct == RMF_UNLINK) { RmFakeUnlink(g_rmFakeList, v); return; }
    *(void**)(v + 0x24) = g_rmFakeRig;
    *(DWORD*)(v + 0x2C) |= g_rmFakeAct == RMF_START_B15 ? 0x8000 : 1;
    if (g_rmFakeAct == RMF_START_DIRTY) g_rmDirty = 1;  // what RmClipStub does for a root-motion clip assignment
}
// EA's sub-step section (0x571C8E..0x571E08) on the fake list
static void RmRefWalk(u8* list, float s) {
    u8* const sent = list - 0x18;
    u8* const head = *(u8**)list;
    *(u8**)(list + 0x10) = head ? head - 0x18 : NULL;
    while (*(u8**)(list + 0x10) != sent) {
        u8* e = *(u8**)(list + 0x10);
        u8* nx = *(u8**)(e + 0x18);
        *(u8**)(list + 0x10) = nx ? nx - 0x18 : NULL;
        const u8* rig = *(const u8* const*)(e + 0x24);
        if (RmFakeTest(e) && rig && *(void* const*)(rig + 0xB0)) RmFakePose(e, NULL, s);
    }
}
static void RmRefSub(u8* list, float d) {
    const float c = *g_rmStepC;
    while (d >= c) d -= c, RmRefWalk(list, c);
    if (d >= *g_rmRemC) RmRefWalk(list, d);
}

static void TestUnseenRootMotion() {
    Ts3nCounters& k = ts3native_counters;
    static Ts3nCounters saved;
    memcpy(&saved, &k, sizeof k);
    const RmStepFn tsSave = g_rmTsOrig, poseSave = g_rmPoseOrig;
    const RmTestFn testSave = g_rmTest;
    const RmInsFn insSave = g_rmInsOrig;
    const float *cSave = g_rmStepC, *rSave = g_rmRemC;
    const DWORD tidSave = g_aaTid;
    static const float c30 = 1.0f / 30, c60 = 1.0f / 60;
    g_rmTsOrig = RmFakeTs, g_rmPoseOrig = RmFakePose, g_rmTest = RmFakeTest, g_rmInsOrig = RmFakeIns, g_rmStepC = &c30, g_rmRemC = &c60;
    g_aaTid = GetCurrentThreadId();
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);

    // EA's step count: dt = k x 1/30 gives k walks (k steps, or k-1 plus the remainder walk on float noise)
    for (int n = 0; n <= 10; n++) {
        bool rem;
        const int s = RmStepsFor((float)n * c30, &rem);
        assert((s == n && !rem) || (n > 0 && s == n - 1 && rem));
    }
    assert(RmKBucket(0) == 0 && RmKBucket(3) == 3 && RmKBucket(5) == 4 && RmKBucket(7) == 5 && RmKBucket(9) == 6 && RmKBucket(10) == 7);

    // list: 6 actors; 1 and 4 root motion with rig (flagged in walk 1), 5 root motion without rig, others none; 6 spare
    enum { N = 7 };
    static u8 ent[N][0x30], rig[0xB4], list0[0x30];
    static RmFakeOwner own[N];
    u8* const list = list0 + 0x18;  // sentinel entry = list0, so list - 0x18 stays inside the buffer
    g_rmFakeList = list, g_rmFakeRig = rig;
    *(void**)(rig + 0xB0) = rig;
    auto build = [&](int n) {
        memset(ent, 0, sizeof ent), memset(list0, 0, sizeof list0);
        for (int i = 0; i < N; i++) own[i].vt = g_rmFakeVt, own[i].ref = 1, *(void**)(ent[i] + 4) = &own[i];
        for (int i = 0; i < n; i++) {
            *(u8**)(ent[i] + 0x18) = i + 1 < n ? ent[i + 1] + 0x18 : list;
            if (i == 1 || i == 4) *(DWORD*)(ent[i] + 0x2C) = 1, *(void**)(ent[i] + 0x24) = rig;
            if (i == 5) *(DWORD*)(ent[i] + 0x2C) = 1;
        }
        *(u8**)list = ent[0] + 0x18;
    };
    auto walk1 = [&](float dt, DWORD passN = 0) {  // pass start + EA's walk 1 through our thunks
        g_rmPassN = passN;  // 0: pass 1, neither sampled (classification needs the game) nor an audit
        RmPassBegin(dt);
        LARGE_INTEGER t;
        QueryPerformanceCounter(&t);
        RmPassClock(t);
        for (u8* e = *(u8**)list - 0x18; e != list - 0x18; e = *(u8**)(e + 0x18) - 0x18) {
            RmTsThunk(e, NULL, dt);
            if (RmFakeTest(e) && *(void**)(e + 0x24)) RmFlagThunk(e);
        }
    };
    struct Log { void* a; float dt; } want[64];
    auto snap = [&](Log* out) { const int n = g_rmFakeN; memcpy(out, g_rmFakeLog, sizeof(Log) * (n < 64 ? n : 64)); return n; };
    auto same = [&](const Log* w, int n) {
        assert(g_rmFakeN == n);
        for (int i = 0; i < n && i < 64; i++) assert(g_rmFakeLog[i].a == w[i].a && g_rmFakeLog[i].dt == w[i].dt);
    };
    // EA alone vs walk 1 + Mode 1 with the same trigger: same calls; returns RmSubSteps' result
    auto versus = [&](int act, u8* trig, u8* vic, DWORD passN) {
        build(6), g_rmFakeAct = act, g_rmFakeTrigger = trig, g_rmFakeVictim = vic, g_rmFakeN = 0;
        RmRefSub(list, 3.0f * c30);
        const int n = snap(want);
        build(6), g_rmFakeAct = act, g_rmFakeTrigger = trig, g_rmFakeVictim = vic, g_rmFakeN = 0;
        walk1(3.0f * c30, passN);
        float d = 3.0f * c30;
        const int r = RmSubSteps(list, &d);
        same(want, n);
        RmPassEnd();
        return r;
    };
    const float dt3 = 3.0f * c30;

    // unchanged list: Mode 1 = EA's calls exactly, refcounts back, no release
    build(6), g_rmFakeN = 0, RmRefSub(list, dt3);
    const int nw = snap(want);
    assert(nw == 6);  // 2 actors x 3 steps
    build(6), k.rmMode = 1, memset(k.rmSlFallback, 0, sizeof k.rmSlFallback), k.rmSlPasses = 0, g_rmFakeN = g_rmFakeRel = 0;
    const LONG fw0 = k.rmSlFullWalks;
    walk1(dt3);
    assert(g_rmSlN == 2 && g_rmNodes == 6);
    float d = dt3;
    assert(RmSubSteps(list, &d) == 1 && k.rmSlPasses == 1);
    same(want, nw);
    for (int i = 0; i < 6; i++) assert(own[i].ref == 1);
    assert(g_rmFakeRel == 0 && k.rmSlFullWalks == fw0);
    RmPassEnd();
    assert(k.rmPassesRm > 0);

    // refcount 0 at the start: EA's +1 / -1 then xchg 1 and release
    build(6), own[1].ref = 0, g_rmFakeN = g_rmFakeRel = 0;
    walk1(dt3);
    d = dt3;
    assert(RmSubSteps(list, &d) == 1 && g_rmFakeRel == 1 && own[1].ref == 1);
    RmPassEnd();

    // off, shortlist full: nothing run here, dt untouched
    build(6), k.rmMode = 0, g_rmFakeN = 0;
    walk1(dt3);
    d = dt3;
    assert(RmSubSteps(list, &d) == 0 && d == dt3 && g_rmFakeN == 0);
    RmPassEnd();
    k.rmMode = 1;
    walk1(dt3);
    g_rmSlFull = true, d = dt3;
    assert(RmSubSteps(list, &d) == 0 && d == dt3 && g_rmFakeN == 0 && k.rmSlFallback[1] == 1);
    RmPassEnd();

    // actor 6 (root motion, rig) linked at the head after walk 1 through the insert hook: stepped as EA does, no fallback
    build(6), *(DWORD*)(ent[6] + 0x2C) = 1, *(void**)(ent[6] + 0x24) = rig, RmFakeIns(list, NULL, ent[6]), g_rmFakeN = 0;
    RmRefSub(list, dt3);
    const int ni = snap(want);
    assert(ni == 9);
    build(6), *(DWORD*)(ent[6] + 0x2C) = 1, *(void**)(ent[6] + 0x24) = rig, g_rmFakeN = 0;
    walk1(dt3);
    const LONG ins = k.rmSlIns, late = k.rmSlLate, lateCalls = k.rmLateRm;
    RmInsThunk(list, NULL, ent[6]);
    d = dt3;
    assert(RmSubSteps(list, &d) == 1 && k.rmSlIns == ins + 1 && k.rmSlLate == late && k.rmLateRm == lateCalls + 3);
    same(want, ni);
    RmPassEnd();

    // a pose call unlinks actor 3 in step 1: the walks go on over the live list, same calls as EA
    assert(versus(RMF_UNLINK, ent[1], ent[3], 0) == 1);
    // a pose call (actor 1, step 1) starts root motion with a clip assignment on a later (3) and an earlier (0) actor
    const LONG fw = k.rmSlFullWalks, late2 = k.rmSlLate, miss = k.rmSlMiss;
    assert(versus(RMF_START_DIRTY, ent[1], ent[3], 0) == 1);
    assert(versus(RMF_START_DIRTY, ent[1], ent[0], 0) == 1);
    assert(k.rmSlFullWalks >= fw + 3 && k.rmSlLate >= late2 + 2 && k.rmSlMiss == miss);
    // bit 15 set by a pose call, no clip assignment: found by the bit read
    const LONG b15 = k.rmSlBit15;
    assert(versus(RMF_START_B15, ent[1], ent[3], 0) == 1 && k.rmSlBit15 >= b15 + 1);
    // unhooked start of an actor in last pass's root-motion set (unseenrm5 misses: walk 1 no, a sub-step yes): on the
    // shortlist through last pass's set, stepped as EA does, a late find, no miss (and no audit needed)
    const LONG late3 = k.rmSlLate;
    RmAdd(g_rmSet[g_rmCur], ent[3]);  // becomes "last pass" at the next RmPassBegin
    assert(versus(RMF_START_SILENT, ent[1], ent[3], 0) == 1 && k.rmSlMiss == miss && k.rmSlLate >= late3 + 1);
    // audit pass (pass 8 of 64): a start no hook sees is stepped as EA does and counted as a miss
    const LONG aud = k.rmSlAudit;
    assert(versus(RMF_START_SILENT, ent[1], ent[3], 7) == 1 && k.rmSlAudit == aud + 1 && k.rmSlMiss == miss + 1);
    // the miss holds EA's full walks up to the next audit (2026-11-02 smoke rmSlMiss=1): next pass starts all-full, counted
    const LONG mh = k.rmSlMissHold;
    assert(g_rmMissHold == 63 && g_rmMissLogs >= 1);
    build(6), g_rmMissHold = 1, g_rmFakeN = 0;
    walk1(c30);
    float dh = c30;
    assert(g_rmAllFull && g_rmMissHold == 0 && g_rmHeld && RmSubSteps(list, &dh) == 1 && k.rmSlMissHold == mh + 1);
    RmPassEnd(), g_rmMissLogs = 0;
    g_rmFakeAct = 0, g_rmFakeTrigger = NULL;

    // walk-1 flip (unseenrm6 misses, docs/228 §10.7): actor 3's test turns yes during walk 1 (e.g. its own non-root-motion
    // pose re-points a composite's child), no clip write, not in last pass's set: the rescan before the first sub-step puts it
    // on the shortlist, EA's calls exactly, rmSlW1Late +1, no miss in a normal (pass 1) or an audit pass (pass 8)
    build(6), *(DWORD*)(ent[3] + 0x2C) = 1, *(void**)(ent[3] + 0x24) = rig, g_rmFakeN = 0;
    RmRefSub(list, dt3);
    const int nf = snap(want);
    assert(nf == 9);
    static const DWORD kPn[] = {0, 7};
    for (DWORD pn : kPn) {
        build(6), g_rmFakeN = 0;
        walk1(c30), RmPassEnd();  // last pass's set = {1, 4}
        const LONG w1 = k.rmSlW1Late, miss2 = k.rmSlMiss;
        walk1(dt3, pn);
        assert(g_rmAudit == (pn == 7) && !g_rmAllFull && !RmHas(g_rmSet[g_rmCur], ent[3]) && !RmHas(g_rmSet[g_rmCur ^ 1], ent[3]));
        *(DWORD*)(ent[3] + 0x2C) = 1, *(void**)(ent[3] + 0x24) = rig;  // the flip, after walk 1's test said no
        g_rmFakeN = 0, d = dt3;
        assert(RmSubSteps(list, &d) == 1 && k.rmSlW1Late == w1 + 1 && k.rmSlMiss == miss2);
        same(want, nf);
        RmPassEnd();
    }
    assert(g_rmMissHold == 0);

    // Mode 0 probe: a pose call for an actor not flagged in walk 1 counts as late root motion; calls pass through
    build(6), k.rmMode = 0, g_rmFakeN = 0;
    walk1(c30);
    const LONG lateRm = k.rmLateRm, all = k.rmCallsAll;
    RmStepThunk(ent[1], NULL, c30), RmStepThunk(ent[5], NULL, c30);
    assert(k.rmLateRm == lateRm + 1 && k.rmCallsAll == all + 2 && g_rmFakeN == 2);
    RmPassEnd();

    // flips: {1, 4} then {4, 2} = 1 ended + 1 started
    build(6);
    walk1(c30);
    RmPassEnd();
    const LONG flips = k.rmFlips;
    walk1(c30);
    memset(g_rmSet[g_rmCur], 0, sizeof g_rmSet[0]), g_rmSlN = 0;  // this pass's set rebuilt as {4, 2}
    RmFlagThunk(ent[4]), RmFlagThunk(ent[2]);
    RmPassEnd();
    assert(k.rmFlips == flips + 2);

    // a sampled pass with owner-less actors (class 2, no game reads): timed, counted, k histogram
    build(6);
    for (int i = 0; i < 6; i++) *(void**)(ent[i] + 4) = NULL;
    k.rmMode = 1;
    const LONG sampled = k.rmSampled, n2 = k.rmN[2];
    walk1(dt3, 15);
    d = dt3;
    assert(RmSubSteps(list, &d) == 1);
    RmPassEnd();
    assert(k.rmSampled == sampled + 1 && k.rmN[2] == n2 + 6 && k.rmTsN > 0 && k.rmFloorN > 0 && k.rmTailN[1] > 0);

    g_rmTsOrig = tsSave, g_rmPoseOrig = poseSave, g_rmTest = testSave, g_rmInsOrig = insSave, g_rmStepC = cSave, g_rmRemC = rSave;
    g_aaTid = tidSave, g_rmFakeTrigger = NULL, g_rmIn = false, g_rmDirty = 0;
    memset(g_rmSet, 0, sizeof g_rmSet), memset(g_rmCand, 0, sizeof g_rmCand), g_rmCandN = 0, memset(g_rmCarry, 0, sizeof g_rmCarry);
    memcpy(&k, &saved, sizeof k);
    printf("unseen root motion: step count, shortlist = EA's calls (unchanged, inserted, unlinked, clip start later/earlier, "
           "bit 15, audit miss, walk-1 flip rescan), fallbacks (off, full), refcount, late, flips, sample ok\n");
}

// TestUnseenRootMotion2: Mode 2 (docs/228 §11) decision and merge mechanics on fakes. The game-side reasons (Rm2WhyGame) are
// replaced by a scripted reason per actor; actors have no owner (no drift, no snap distance, no game reads). Checks: v154
// layout; RmEff (Mode 1 unchanged without Mode 2); hysteresis (HystPasses exact passes before coarse, reset by a reason);
// merge schedule and time kept (k = 10: 4c, 4c, 2c; with the remainder walk: 4c, 4c, 2c + rem); an exact actor = EA's calls;
// off arm = EA's calls but counted eligible; snap (switch off mid-pass: owed steps one 1/30 s step each, then the step); lost
// owed steps at the pass end; slot taken by another actor's owed steps = exact; camera found / jump / lost holds, lead
// speed, margin radius.
static int g_rm2FakeWhy[2];
static u8 g_rm2FakeAct[2][0x30];
static int Rm2FakeWhy(void* a) { return a == g_rm2FakeAct[0] ? g_rm2FakeWhy[0] : a == g_rm2FakeAct[1] ? g_rm2FakeWhy[1] : -1; }
static const float *g_rm2FakeSteerP, *g_rm2FakeSteerQ;
static void __fastcall Rm2FakeSteer(void*, void*, const float* p, const float* q) { g_rm2FakeSteerP = p, g_rm2FakeSteerQ = q; }
static void TestUnseenRootMotion2() {
    static_assert(offsetof(Ts3nCounters, rm2Why) == 0x5984 && offsetof(Ts3nCounters, rm2Elig) == 0x59C4 &&
                  offsetof(Ts3nCounters, rm2DriftN) == 0x5A00 && offsetof(Ts3nCounters, rm2Faults) == 0x5A48 &&
                  offsetof(Ts3nCounters, rm2EndN) == 0x5A4C && offsetof(Ts3nCounters, rm2End30) == 0x5A54 && sizeof(Ts3nCounters) == 0x6258, "counters v154 layout after v153 (docs/registry.md)");
    Ts3nCounters& k = ts3native_counters;
    static Ts3nCounters saved;
    memcpy(&saved, &k, sizeof k);
    const RmStepFn tsSave = g_rmTsOrig, poseSave = g_rmPoseOrig;
    const RmTestFn testSave = g_rmTest;
    const float *cSave = g_rmStepC, *rSave = g_rmRemC;
    const DWORD tidSave = g_aaTid;
    const auto rm2Save = g_rm2;
    static const float c30 = 1.0f / 30, c60 = 1.0f / 60;
    g_rmTsOrig = RmFakeTs, g_rmPoseOrig = RmFakePose, g_rmTest = RmFakeTest, g_rmStepC = &c30, g_rmRemC = &c60, g_rm2WhyFn = Rm2FakeWhy;
    g_aaTid = GetCurrentThreadId(), g_rmFakeTrigger = NULL;
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    static u8 rig[0xB4];
    *(void**)(rig + 0xB0) = rig;
    memset(g_rm2FakeAct, 0, sizeof g_rm2FakeAct);
    for (auto& a : g_rm2FakeAct) *(DWORD*)(a + 0x2C) = 1, *(void**)(a + 0x24) = rig;  // root motion, owner NULL
    u8* const a0 = g_rm2FakeAct[0];
    u8* const a1 = g_rm2FakeAct[1];

    // RmEff: without Mode 2, Mode 1 as before (2 = off); with Mode 2 every nonzero switch value = 2
    g_rm2.on = false, k.rmMode = 1;
    assert(RmEff() == 1);
    k.rmMode = 2;
    assert(RmEff() == 0);
    g_rm2.on = true, k.rmMode = 1;
    assert(RmEff() == 2);
    k.rmMode = 0;
    assert(RmEff() == 0);

    g_rm2.mergeSteps = 4, g_rm2.hystPasses = 2, g_rm2.camIcPasses = 3, g_rm2.marginPct = 25, g_rm2.leadMs = 500, g_rm2.place = 0;
    memset(g_rm2Slot, 0, sizeof g_rm2Slot), g_rm2Open = 0, g_rm2DtCarry = g_rm2LostCarry = 0;
    k.rmMode = 2;
    auto begin = [&](float dt) {  // pass start as AaPassThunk does it
        RmPassBegin(dt);
        LARGE_INTEGER t;
        QueryPerformanceCounter(&t);
        RmPassClock(t);
        g_rmFakeN = 0;
    };
    // one pass of EA's loop for a0 and a1: dt = n x 1/30 + rem; a0 gets calls only while stop < 0 or fewer than stop; the
    // switch goes off before a0's call number flip and back to 2 after the pass
    auto run = [&](int n, float rem, int stop = -1, int flip = -1) {
        const float dt = (float)n * c30 + rem;
        begin(dt);
        RmFlagThunk(a0), RmFlagThunk(a1);
        float d = dt;
        int calls = 0;
        while (d >= c30) {
            d -= c30;
            if (stop < 0 || calls < stop) {
                if (calls == flip) k.rmMode = 0;
                RmStepThunk(a0, NULL, c30), calls++;
            }
            RmStepThunk(a1, NULL, c30);
        }
        if (d >= c60) RmRemThunk(a0, NULL, d), RmRemThunk(a1, NULL, d);
        RmPassEnd();
        k.rmMode = 2;
    };
    // a0's calls in the last pass: count, sum of dt, each dt in 1/30 steps (rounded)
    auto calls0 = [&](float* sum, int* steps, int max) {
        int n = 0;
        *sum = 0;
        for (int i = 0; i < g_rmFakeN && i < 64; i++)
            if (g_rmFakeLog[i].a == a0) {
                if (n < max) steps[n] = (int)(g_rmFakeLog[i].dt / c30 + 0.5f);
                *sum += g_rmFakeLog[i].dt, n++;
            }
        return n;
    };
    float sum;
    int st[16];

    // hysteresis: a0 eligible, a1 exact (SACS); passes 1-2 exact (hyst), pass 3 coarse 4c 4c 2c, a1 always EA's 10 calls
    g_rm2FakeWhy[0] = -1, g_rm2FakeWhy[1] = RM2_SACS;
    const LONG hyst = k.rm2Why[RM2_HYST], sacs = k.rm2Why[RM2_SACS], elig = k.rm2Elig, cp = k.rm2CoarsePasses;
    run(10, 0);
    assert(calls0(&sum, st, 16) == 10 && g_rmFakeN == 20);
    run(10, 0);
    assert(calls0(&sum, st, 16) == 10 && k.rm2Why[RM2_HYST] == hyst + 2 && k.rm2Why[RM2_SACS] == sacs + 2 && k.rm2Elig == elig);
    const LONG co = k.rm2Coarse, ow = k.rm2Owed;
    run(10, 0);
    assert(calls0(&sum, st, 16) == 3 && st[0] == 4 && st[1] == 4 && st[2] == 2 && fabsf(sum - 10 * c30) < 1e-5f && g_rmFakeN == 13);
    assert(k.rm2Coarse == co + 3 && k.rm2Owed == ow + 7 && k.rm2Elig == elig + 1 && k.rm2CoarsePasses == cp + 1 && g_rm2Open == 0);
    // with the remainder walk: 4c, 4c, then 2c + rem in the remainder call
    run(10, 0.02f);
    assert(calls0(&sum, st, 16) == 3 && st[0] == 4 && st[1] == 4 && fabsf(sum - (10 * c30 + 0.02f)) < 1e-5f);
    // k = 1: nothing to merge, one call with its own dt
    run(1, 0);
    assert(calls0(&sum, st, 16) == 1 && st[0] == 1);

    // off arm: EA's calls, still counted eligible, not coarse
    const LONG elig2 = k.rm2Elig, cp2 = k.rm2CoarsePasses;
    k.rmMode = 0;
    begin(10 * c30);
    RmFlagThunk(a0), RmFlagThunk(a1);
    for (int i = 0; i < 10; i++) RmStepThunk(a0, NULL, c30);
    RmPassEnd();
    assert(g_rmFakeN == 10 && k.rm2Elig == elig2 + 1 && k.rm2CoarsePasses == cp2);
    k.rmMode = 2;

    // snap: switch off before a0's 3rd call (2 owed): 2 single steps, then the step, then EA's 7 = 10 calls of 1/30 s
    run(10, 0);  // the off pass broke nothing: still coarse
    assert(calls0(&sum, st, 16) == 3);
    const LONG snap = k.rm2Snap, snapSteps = k.rm2SnapSteps;
    run(10, 0, -1, 2);
    assert(calls0(&sum, st, 16) == 10 && fabsf(sum - 10 * c30) < 1e-5f && k.rm2Snap == snap + 1 && k.rm2SnapSteps == snapSteps + 2);
    for (int i = 0; i < 10; i++) assert(st[i] == 1);

    // lost: a0 gets 2 calls (both owed) and then none: dropped at the pass end, counted
    const LONG lost = k.rm2Lost, lostMs = k.rm2LostMs;
    run(10, 0, 2);
    assert(calls0(&sum, st, 16) == 0 && k.rm2Lost == lost + 1 && k.rm2LostMs >= lostMs + 66 && g_rm2Open == 0);

    // a reason resets the streak: one goal pass, then HystPasses exact passes again, then coarse
    g_rm2FakeWhy[0] = RM2_GOAL;
    run(10, 0);
    assert(calls0(&sum, st, 16) == 10);
    g_rm2FakeWhy[0] = -1;
    run(10, 0);
    run(10, 0);
    assert(calls0(&sum, st, 16) == 10);
    run(10, 0);
    assert(calls0(&sum, st, 16) == 3);

    // slot taken: two actors with the same hash; the first owes a step, the second runs exact this pass
    static u8 pool[1 << 18];
    static int seen[RM_SET];
    u8 *pa = NULL, *pb = NULL;
    memset(seen, 0xFF, sizeof seen);
    for (int i = 0; i < (int)(sizeof pool / 0x40) && !pb; i++) {
        const unsigned h = RmHash(pool + i * 0x40);
        if (seen[h] >= 0) pa = pool + seen[h] * 0x40, pb = pool + i * 0x40;
        else seen[h] = i;
    }
    assert(pa && pb && RmHash(pa) == RmHash(pb));
    for (int i = 0; i < 3; i++) {  // pa: 3 eligible passes in a row (past the hysteresis)
        begin(c30);
        RmStepThunk(pa, NULL, c30);
        RmPassEnd();
    }
    const LONG slot = k.rm2Why[RM2_SLOT], lost2 = k.rm2Lost;
    begin(10 * c30);
    RmStepThunk(pa, NULL, c30);  // owed
    RmStepThunk(pb, NULL, c30);  // slot taken: exact
    assert(g_rmFakeN == 1 && g_rmFakeLog[0].a == pb && k.rm2Why[RM2_SLOT] == slot + 1);
    RmPassEnd();
    assert(k.rm2Lost == lost2 + 1);

    // camera: found = hold CamIcPasses; small moves count it down and set the lead speed; a 50 m move = jump; a reach jump
    // (map view) = jump; lost = hold, not a jump
    memset(&g_rm2Cam, 0, sizeof g_rm2Cam);
    float cpos[3] = {0, 10, 0}, ctgt[3] = {0, 0, 20};
    const LONG jumps = k.rm2CamJump;
    Rm2CamStep(cpos, ctgt, 80, true, 0);
    assert(g_rm2Cam.hold == 3 && k.rm2CamJump == jumps + 1);
    for (int i = 0; i < 3; i++) cpos[0] += 1, ctgt[0] += 1, Rm2CamStep(cpos, ctgt, 80, true, 0.05f);
    assert(g_rm2Cam.hold == 0 && g_rm2Cam.speed > 19 && g_rm2Cam.speed < 21);
    cpos[0] += 50, ctgt[0] += 50;
    Rm2CamStep(cpos, ctgt, 80, true, 0.05f);
    assert(g_rm2Cam.hold == 3 && k.rm2CamJump == jumps + 2);
    for (int i = 0; i < 3; i++) Rm2CamStep(cpos, ctgt, 80, true, 0.05f);
    assert(g_rm2Cam.hold == 0);
    Rm2CamStep(cpos, ctgt, 300, true, 0.05f);
    assert(g_rm2Cam.hold == 3 && k.rm2CamJump == jumps + 3);
    Rm2CamStep(cpos, ctgt, 300, false, 0.05f);
    assert(g_rm2Cam.hold == 3 && k.rm2CamJump == jumps + 3);
    assert(fabsf(Rm2MarginR(100, 0) - 16.0f) < 1e-4f && fabsf(Rm2MarginR(0, 5) - 6.0f) < 1e-4f);
    // v155 script camera ICs: a hit asks CamIcPasses + lerp time x 30 (atomic max), the next pass start takes it; a jump
    // during a longer IC hold keeps the longer one; NaN / > 10 s lerp times add nothing
    static_assert(offsetof(Ts3nCounters, rm2CamIc) == 0x5A5C && offsetof(Ts3nCounters, rm2CamIcOff) == 0x5A88 &&
                  sizeof(Ts3nCounters) == 0x6258 && RM2_IC_N == 11, "counters v155 layout after v154 (docs/registry.md)");
    for (int i = 0; i < 4; i++) Rm2CamStep(cpos, ctgt, 300, true, 0.05f);
    assert(g_rm2Cam.hold == 0);
    const LONG ic1 = k.rm2CamIc[1], ic9 = k.rm2CamIc[9];
    DWORD icArgs[4] = {0, 0, 0, 0};
    const float half = 0.5f, nan = sqrtf(-1.0f), big = 60.0f;
    memcpy(&icArgs[3], &half, 4);
    Rm2CamIcHit(1, icArgs);  // RequestLerpToTarget(target, 0.5 s): 3 + 15
    memcpy(&icArgs[0], &nan, 4);
    Rm2CamIcHit(9, icArgs);  // EnableMapViewMode(NaN): 3, smaller, kept 18
    assert(g_rm2IcHold == 18 && k.rm2CamIc[1] == ic1 + 1 && k.rm2CamIc[9] == ic9 + 1 && g_rm2Cam.hold == 0);
    cpos[0] += 50, ctgt[0] += 50;  // a jump in the same pass: max(3, 18)
    Rm2CamStep(cpos, ctgt, 300, true, 0.05f);
    assert(g_rm2Cam.hold == 18 && g_rm2IcHold == 0);
    memcpy(&icArgs[0], &big, 4);
    Rm2CamIcHit(9, icArgs);  // 60 s: ignored, 3 < 17 left
    Rm2CamStep(cpos, ctgt, 300, true, 0.05f);
    assert(g_rm2Cam.hold == 17);
    g_rm2IcHold = 0;
    memset(&g_rm2Cam, 0, sizeof g_rm2Cam);

    // v157 steering record: 0x954580 is thiscall(self; p*, q*) [verified, I-420-17]; a synthetic write records q as a unit
    // quaternion (rm2QuatBad unchanged) and calls EA's in the same order; swapped arguments count as bad
    static_assert(offsetof(Ts3nCounters, rm2SacsHold) == 0x5A98 && offsetof(Ts3nCounters, rm2SacsAct) == 0x5AAC &&
                  sizeof(Ts3nCounters) == 0x6258, "counters v157 layout after v156 (docs/registry.md)");
    {
        static u8 steer[8];
        *(void**)steer = rig;  // owner = [self - 4]
        __declspec(align(16)) static const float p[4] = {10, 20, 30, 0}, q[4] = {0, 0, 0.70710678f, 0.70710678f};
        const Rm2SteerFn so = g_rm2SteerOrig;
        g_rm2SteerOrig = Rm2FakeSteer, g_rm2.on = true;
        const LONG recs = k.rm2RouteRecs, bad = k.rm2QuatBad;
        Rm2SteerThunk(steer + 4, NULL, p, q);
        const Rm2Route& r = g_rm2Route[RmHash(rig) & (RM2_ROUTES - 1)];
        assert(k.rm2RouteRecs == recs + 1 && k.rm2QuatBad == bad && r.owner == rig && !memcmp(r.q, q, 16) && !memcmp(r.p, p, 16));
        assert(g_rm2FakeSteerP == p && g_rm2FakeSteerQ == q);
        Rm2SteerThunk(steer + 4, NULL, q, p);  // swapped: arg 2 (p) is not unit length
        assert(k.rm2QuatBad == bad + 1 && k.rm2RouteRecs == recs + 2);
        g_rm2SteerOrig = so;
        memset(g_rm2Route, 0, sizeof g_rm2Route);
    }
    // v157 SACS hold on a fake component (docs/228 §11.7): driver A binds the Sim only; driver B binds the Sim and an object
    // (id high word only) next to an unbound slot
    {
        static u8 hs[3][0x18], sl[3][0xC], drvA[0x18], drvB[0x18], sc[0x10];
        static void *slotsA[2], *slotsB[6], *drivers[2];
        *(DWORD*)(hs[0] + 0x10) = 7, *(DWORD*)(hs[1] + 0x14) = 9;  // hs[2] unbound (id 0)
        for (int i = 0; i < 3; i++) *(void**)(sl[i] + 8) = hs[i];
        slotsA[1] = sl[0], slotsB[1] = sl[0], slotsB[3] = sl[2], slotsB[5] = sl[1];
        *(void**)(drvA + 0x10) = slotsA, *(void**)(drvA + 0x14) = slotsA + 2;
        *(void**)(drvB + 0x10) = slotsB, *(void**)(drvB + 0x14) = slotsB + 6;
        drivers[0] = drvA, drivers[1] = drvB;
        const auto comp = [&](int n) { *(void**)(sc + 8) = drivers, *(void**)(sc + 0xC) = drivers + n; return (const u8*)sc; };
        LONG d[4], a[5];
        memcpy(d, k.rm2SacsDrv, sizeof d), memcpy(a, k.rm2SacsAct, sizeof a);
        assert(!Rm2SacsHold(NULL, 2) && !Rm2SacsHold(comp(0), 1));                                   // 0 drivers, 0 actors (x2)
        assert(!Rm2SacsHold(comp(1), 2) && Rm2SacsHold(comp(1), 1) && !Rm2SacsHold(comp(1), 0));    // 1 driver, 1 actor (x3)
        assert(Rm2SacsHold(comp(2), 2) && !Rm2SacsHold(comp(2), 0));                                 // 2 drivers, 2 actors (x2)
        assert(k.rm2SacsDrv[0] == d[0] + 2 && k.rm2SacsDrv[1] == d[1] + 3 && k.rm2SacsDrv[2] == d[2] + 2 && k.rm2SacsDrv[3] == d[3]);
        assert(k.rm2SacsAct[0] == a[0] + 2 && k.rm2SacsAct[1] == a[1] + 3 && k.rm2SacsAct[2] == a[2] + 2 && k.rm2SacsAct[4] == a[4]);
        // v159 SacsHold=3 (docs/228 §11.8): id 7 = the Sim itself, id hi 9 = the scripted class; hold only on other Sim,
        // slotted elsewhere, object near, not found, fault; every bound actor counted once per decision in rm2SacsCls
        static_assert(offsetof(Ts3nCounters, rm2SacsCls) == 0x5AC4 && sizeof(Ts3nCounters) == 0x6258 && RM2C_N == 8,
                      "counters v159 layout after v158 (docs/registry.md)");
        static int s_cls;
        const auto fake = [](void*, DWORD lo, DWORD hi, const float*) -> int { return lo == 7 && !hi ? RM2C_SELF : s_cls; };
        const auto clsSave = g_rm2ActCls;
        g_rm2ActCls = fake;
        static const float pos[3] = {};
        LONG c[RM2C_N];
        memcpy(c, k.rm2SacsCls, sizeof c);
        assert(!Rm2SacsHold(comp(1), 3, rig, pos));  // the Sim alone: coarse
        assert(!Rm2SacsHold(comp(2), 3));            // no owner: nothing classified, no hold
        for (int i = 0; i < RM2C_N; i++) {
            s_cls = i;
            const bool want = i == RM2C_SIM || i == RM2C_SLOTTED || i == RM2C_NEAR || i == RM2C_NOOBJ || i == RM2C_FAULT;
            assert(Rm2SacsHold(comp(2), 3, rig, pos) == want);
            assert(Rm2SacsHold(comp(2), 2, rig, pos));  // mode 2 unchanged, classes still counted
        }
        assert(k.rm2SacsCls[RM2C_SELF] == c[RM2C_SELF] + 1 + RM2C_N * 4 + 2);  // 2 self slots per call, 2 calls per class; the i == SELF round adds 2
        for (int i = 1; i < RM2C_N; i++) assert(k.rm2SacsCls[i] == c[i] + 2);
        g_rm2ActCls = clsSave;
    }

    g_rmTsOrig = tsSave, g_rmPoseOrig = poseSave, g_rmTest = testSave, g_rmStepC = cSave, g_rmRemC = rSave, g_rm2WhyFn = Rm2WhyGame;
    g_aaTid = tidSave, g_rmIn = false, g_rm2 = rm2Save;
    memset(g_rm2Slot, 0, sizeof g_rm2Slot), g_rm2Open = 0, g_rm2DtCarry = g_rm2LostCarry = 0, memset(g_rm2Vis, 0, sizeof g_rm2Vis);
    memset(g_rmSet, 0, sizeof g_rmSet), memset(g_rmCand, 0, sizeof g_rmCand), g_rmCandN = 0, memset(g_rmCarry, 0, sizeof g_rmCarry);
    memcpy(&k, &saved, sizeof k);
    printf("unseen root motion Mode 2: layout, RmEff, hysteresis, merge 4/4/2 (+remainder, k=1), off arm, snap, lost, reason "
           "reset, slot taken, camera found/jump/reach/lost, margin radius, steer arg order, SACS hold 0/1/2 ok\n");
}
