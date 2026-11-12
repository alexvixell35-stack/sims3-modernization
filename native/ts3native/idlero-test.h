// --- [IdleCheckRO] (idlero.cpp, docs/178 §8): layout; both call-site stubs on fakes (the game's 0x62FB60 and 0x625140 and
// the lot-table read replaced): pass-through rules, A's answer, B under QL, fallbacks, B's redo, shadow compare. Included by
// selftest.cpp.
namespace ikt {
static LONG gipCalls, testCalls, entryCalls, entryWhy, testRet;
static u8 entryA[0x100], entryB[0x100];
static u8* gipRet;
static const u8* entryRet;
static const u8* testEntry;
static DWORD gipLo, gipHi, testRoom, testB;
static const void* testPt;
static float testR;
static bool testWrites;  // a tripwire'd writer runs beside 0x625140
static u8* __fastcall Gip(u8*, void*, DWORD lo, DWORD hi) { gipCalls++, gipLo = lo, gipHi = hi; return gipRet; }
static DWORD __fastcall Test(u8*, void*, const u8* e, DWORD room, DWORD b, const void* pt, float r) {
    testCalls++, testEntry = e, testRoom = room, testB = b, testPt = pt, testR = r;
    if (testWrites) InterlockedIncrement(&g_twEpoch);
    return (DWORD)testRet | 0xABCD00;
}
static int Entry(const u8*, DWORD, DWORD, const u8** e) {
    entryCalls++;
    *e = entryRet;
    return entryWhy;
}
static HANDLE holdIn, holdGo;
static DWORD WINAPI HoldQl(void*) {
    AcquireSRWLockExclusive(&g_ql);
    SetEvent(holdIn);
    WaitForSingleObject(holdGo, INFINITE);
    ReleaseSRWLockExclusive(&g_ql);
    return 0;
}
}  // namespace ikt

static void TestIdleCheckRO() {
    using namespace ikt;
    Ts3nCounters& k = ts3native_counters;
    static_assert(offsetof(Ts3nCounters, gvReady) == 0x47DC && offsetof(Ts3nCounters, ikReady) == 0x4824 &&
                      offsetof(Ts3nCounters, ikMode) == 0x4828 && offsetof(Ts3nCounters, ikShadow) == 0x482C &&
                      offsetof(Ts3nCounters, ikOnward) == 0x4840 && offsetof(Ts3nCounters, ikFb) == 0x484C &&
                      offsetof(Ts3nCounters, ikShadowMismatch) == 0x4868 && sizeof(Ts3nCounters) == 6294 * 4,
                  "counters v119 layout after v118 (docs/registry.md)");
    assert(k.version == 912);
    assert(kIkSites[0].site == 0x007558D7 && kIkSites[0].target == 0x0062FB60);
    assert(kIkSites[1].site == 0x00755916 && kIkSites[1].target == 0x00625140);
    memset(&k.ikReady, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, ikReady));
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    const IkGipFn savedGip = g_ikGip;
    const IkTestFn savedTest = g_ikTest;
    const IkEntryFn savedEntry = g_ikEntry;
    const AsyncSlot savedAsync = g_async;
    const LONG savedArmed = g_twArmed, savedOwner = k.rgOwnerTid;
    g_ikGip = Gip, g_ikTest = Test, g_ikEntry = Entry;
    const DWORD me = GetCurrentThreadId();
    g_async.owner = me;
    k.rgOwnerTid = 0;
    static u8 planner[0x60];
    u8* const P = planner;
    const float pt[4] = {1.0f, 2.0f, 3.0f, 0.0f};
    auto a = [&]() { return IkGipStub(P, NULL, 0x30, 0x1); };
    auto b = [&](const u8* e) { return IkTestStub(P, NULL, e, 7, 0x55, pt, 0.5f); };

    // switches off / another thread: the game's calls, nothing counted, B stays plain
    gipRet = entryB, testRet = 1;
    assert(a() == entryB && gipCalls == 1 && k.ikCalls == 0 && !g_ikA.set);
    assert(b(entryB) == (0xABCD00 | 1) && testCalls == 1);
    k.ikMode = 1, g_async.state = A_RUNNING, g_async.owner = me + 1;
    assert(a() == entryB && gipCalls == 2 && k.ikCalls == 0);
    g_async.owner = me;

    // a job runs: A answers from the table read, no game lookup; B runs the game's leaf on A's entry under QL shared
    g_twArmed = 1, entryWhy = SR_OK, entryRet = entryA + 0x10;
    assert(a() == entryA + 0x10 && gipCalls == 2 && entryCalls == 1 && k.ikTaken == 1 && k.ikHits == 1 && g_ikA.set);
    testRet = 1;
    assert(b(entryA + 0x10) == (0xABCD00 | 1) && testCalls == 2 && testEntry == entryA + 0x10 && testRoom == 7 && testB == 0x55 &&
           testPt == pt && testR == 0.5f && k.ikPass == 1 && !g_ikA.set);
    assert(TryAcquireSRWLockExclusive(&g_ql));  // B released QL
    ReleaseSRWLockExclusive(&g_ql);
    a();
    testRet = 0;
    assert(b(entryA + 0x10) == 0xABCD00 && k.ikOnward == 1 && gipCalls == 2);  // 0: the IC goes on to 0x82EDB0
    entryRet = NULL;  // Contains false: 0, no B
    assert(a() == NULL && k.ikTaken == 3 && k.ikOnward == 2 && !g_ikA.set && gipCalls == 2);
    // B with no A answer before it (or another entry): the game's leaf, plain
    assert(b(entryB) == 0xABCD00 && testCalls == 4 && testEntry == entryB);
    entryRet = entryA + 0x10;
    a();
    assert(b(entryB) == 0xABCD00 && testEntry == entryB && k.ikFb[IK_FB_REDO] == 0 && !g_ikA.set);

    // A's fallbacks: the game's lookup, counted by reason
    gipRet = entryB;
    struct Fb { const char* name; int why; int idx; };
    static const Fb fbs[] = {{"table shape", SR_FB_SHAPE, IK_FB_SHAPE}, {"tripwire", SR_FB_TRIP, IK_FB_TRIP}};
    for (const Fb& f : fbs) {
        entryWhy = f.why;
        const LONG g0 = gipCalls, f0 = k.ikFb[f.idx];
        if (a() != entryB || gipCalls != g0 + 1 || k.ikFb[f.idx] != f0 + 1 || g_ikA.set) {
            printf("IdleCheckRO fallback '%s' not taken\n", f.name);
            assert(false);
        }
    }
    entryWhy = SR_OK;
    {
        const LONG g0 = gipCalls, s0 = k.ikFb[IK_FB_STATE];
        k.rgOwnerTid = (LONG)me;  // the sim owns +0x918 (lent or recursive): the game's path
        assert(a() == entryB && gipCalls == g0 + 1 && k.ikFb[IK_FB_STATE] == s0 + 1);
        k.rgOwnerTid = 0;
        g_twArmed = 0;  // the job ended (tripwires disarmed): writers unseen
        assert(a() == entryB && gipCalls == g0 + 2 && k.ikFb[IK_FB_JOBEND] == 1);
        g_twArmed = 1;
        AcquireSRWLockExclusive(&g_ql);  // a writer holds QL: try only
        assert(a() == entryB && gipCalls == g0 + 3 && k.ikFb[IK_FB_TRY] == 1);
        ReleaseSRWLockExclusive(&g_ql);
        k.ikMode = 0;  // switch off with a job: the game's lookup
        assert(a() == entryB && gipCalls == g0 + 4);
        k.ikMode = 1;
    }

    // B's redo: something moved between A and B, or during the leaf -> the game's lookup, then the leaf on its entry
    {
        testRet = 1;
        const LONG g0 = gipCalls, r0 = k.ikFb[IK_FB_REDO], p0 = k.ikPass;
        assert(a() == entryA + 0x10);
        InterlockedIncrement(&g_qlSeq);  // a QL writer ran between A and B
        gipRet = entryB;
        assert(b(entryA + 0x10) == (0xABCD00 | 1) && gipCalls == g0 + 1 && gipLo == 0x30 && gipHi == 1 && testEntry == entryB &&
               k.ikFb[IK_FB_REDO] == r0 + 1 && k.ikFb[IK_FB_TRIP] == 2 && k.ikPass == p0);
        a();
        gipRet = NULL;  // the game's lookup misses now: 0 (the caller's 0x75592B path), no leaf
        InterlockedIncrement(&g_twGen);  // the job ended between A and B
        const LONG t0 = testCalls;
        assert(b(entryA + 0x10) == 0 && testCalls == t0 && k.ikFb[IK_FB_JOBEND] == 2 && k.ikFb[IK_FB_REDO] == r0 + 2);
        gipRet = entryB;
        a();
        testWrites = true;  // a tripwire'd writer during the leaf: its answer is dropped, the leaf runs again on the game's entry
        assert(b(entryA + 0x10) == (0xABCD00 | 1) && testCalls == t0 + 2 && testEntry == entryB && k.ikFb[IK_FB_REDO] == r0 + 3);
        testWrites = false;
        a();
        holdIn = CreateEventW(NULL, TRUE, FALSE, NULL), holdGo = CreateEventW(NULL, TRUE, FALSE, NULL);
        HANDLE th = CreateThread(NULL, 0, HoldQl, NULL, 0, NULL);  // a writer takes QL between A and B
        WaitForSingleObject(holdIn, INFINITE);
        const LONG g1 = gipCalls, y0 = k.ikFb[IK_FB_TRY];
        assert(b(entryA + 0x10) == (0xABCD00 | 1) && gipCalls == g1 + 1 && testEntry == entryB && k.ikFb[IK_FB_TRY] == y0 + 1 &&
               k.ikFb[IK_FB_REDO] == r0 + 4);
        SetEvent(holdGo);
        WaitForSingleObject(th, INFINITE);
        CloseHandle(th), CloseHandle(holdIn), CloseHandle(holdGo);
        a();
        assert(b(entryA + 0x10) == (0xABCD00 | 1) && gipCalls == g1 + 1 && k.ikPass == p0 + 1);
    }

    // shadow with no job: the game's answer is returned, the table read compared; a skew is one mismatch
    k.ikShadow = 1, g_async.state = A_IDLE, g_twArmed = 0;
    gipRet = entryA + 0x10, entryRet = entryA + 0x10;
    const LONG g0 = gipCalls;
    assert(a() == entryA + 0x10 && gipCalls == g0 + 1 && k.ikShadowRuns == 1 && k.ikShadowMismatch == 0 && g_rgOffSimOpen == 0 && !g_ikA.set);
    entryRet = NULL;
    assert(a() == entryA + 0x10 && k.ikShadowRuns == 2 && k.ikShadowMismatch == 1 && g_rgOffSimOpen == 0);
    gipRet = NULL;
    assert(a() == NULL && k.ikShadowRuns == 3 && k.ikShadowMismatch == 1);
    // shortcut: no threaded case here; the QL protocol is S2's (steerro-test, obstructro and rtoro threaded runs)
    printf("IdleCheckRO ([IdleCheckRO] Enabled=1): counters v119; switch off / other thread pass; job: lookup answered from the "
           "table read, leaf on A's entry under QL (pass / onward), miss = 0 with no leaf; fallbacks (shape, tripwire, +0x918 "
           "owner, job end, QL busy, switch off) call the game; B redo on QL writer / job end / tripwire in the leaf (game "
           "lookup + leaf, miss = 0); shadow compare + skew\n");

    k.ikMode = k.ikShadow = 0;
    g_ikGip = savedGip, g_ikTest = savedTest, g_ikEntry = savedEntry;
    g_async = savedAsync, g_twArmed = savedArmed, k.rgOwnerTid = savedOwner;
}
