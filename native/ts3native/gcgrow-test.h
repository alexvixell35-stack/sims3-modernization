// TestGcGrowLog ([GcGrowLog], gcgrow.cpp, docs/232 Run 8l). Included by selftest.cpp.
// The thunk runs for real from a fake GC_collect_or_expand frame (esi = needed, one pushed argument, add esp,4 after the
// call): the fake expand grows a fake heap; counters, failure, growth outside our calls, the line limits, the 600 MB ring,
// the refusal text on fake Boehm globals, the LoadXmlAsString key, the expand streak and GcBlackOk ([GC] BlackListCapKB) are checked.

static DWORD g_gwFakeHeap, g_gwFakeAsk;
static int __cdecl GwFakeExpand(DWORD blocks) {
    g_gwFakeAsk = blocks;
    if (blocks == 3) return 0;  // "out of address space"
    g_gwFakeHeap += blocks << 12;
    return 1;
}
static __declspec(naked) int __stdcall GwRunSite(DWORD /*blocks*/, DWORD /*needed*/) {
    __asm {
        push esi
        mov esi, [esp + 12]
        push dword ptr [esp + 8]
        call GwThunk
        add esp, 4
        cmp esi, [esp + 12]  // esi kept
        je kept
        mov eax, -1
    kept:
        pop esi
        ret 8
    }
}
static int g_gwWarned;
static void __cdecl GwFakeWarn(const char*, DWORD bytes) { assert(bytes == 969u << 12); g_gwWarned++; }
static DWORD __cdecl GwFakeOne() { return 1; }
static DWORD __cdecl GwFakeAdj() { return 5000; }
static DWORD __cdecl GwFakeMin() { return 4000; }
static DWORD g_gwFakeStr[3];
static DWORD g_gwXmlSeen[4];
static DWORD __cdecl GwFakeXml(DWORD lo, DWORD hi, DWORD group, DWORD type) {
    g_gwXmlSeen[0] = lo, g_gwXmlSeen[1] = hi, g_gwXmlSeen[2] = group, g_gwXmlSeen[3] = type;
    assert(g_gwXmlTid == GetCurrentThreadId() && g_gwXmlKey[0] == hi && g_gwXmlKey[1] == lo && g_gwXmlGT[1] == type);
    return (DWORD)(uintptr_t)g_gwFakeStr;
}

static void TestGcGrowLog() {
    static_assert(offsetof(Ts3nCounters, gcBlackOk) == 0x5B14 && offsetof(Ts3nCounters, gwMode) == 0x5AE8 && offsetof(Ts3nCounters, gwUnseenKB) == 0x5B00 &&
                      offsetof(Ts3nCounters, gwXmlMaxKB) == 0x5B0C && sizeof(Ts3nCounters) == 6066 * 4,
                  "counters v161-v163 layout (docs/registry.md)");
    Ts3nCounters& k = ts3native_counters;
    const MxAddrs saved = g_mx;
    const GwBoehm savedB = g_gwb;
    DWORD gcNo = 7, words = 0x100, largeFree = 0, zero = 0, req = 100u << 20, at = 0xFFFFFFFF, la = 4u << 20, maxLa = 900u << 20,
          fin = 0, bl = 8u << 20, supp = 0, ival = 5, freeBytes[61] = {0};
    freeBytes[60] = 300u << 20;
    g_mx.heapSize = &g_gwFakeHeap, g_mx.gcNo = &gcNo, g_mx.wordsAllocd = &words;
    g_gwb = {&zero, &zero, &req, &at, &la, &maxLa, freeBytes, &fin, &bl, &supp, &ival, GwFakeOne, GwFakeAdj, GwFakeMin};
    g_gwLargeFree = &largeFree;
    g_gwExpand = GwFakeExpand;
    g_gwFakeHeap = 352u << 20, g_gwLastHeap = 0, g_gwRingN = 0, g_gwCrossed = false;
    memset(&k.gwMode, 0, sizeof(LONG) * 18);
    k.gwMode = 1;
    assert(GwListIdx(969) == 60 && GwListIdx(32) == 32 && GwListIdx(33) == 32 && GwListIdx(255) == 59);
    char why[700];
    GwWhy(969, 352u << 20, 0, why, sizeof(why));  // 4 + 300 <= 900 MB: not enough large bytes left
    assert(strstr(why, "gate ON") && strstr(why, "enough large left NO") && !strstr(why, "REFUSE") && strstr(why, "within it"));
    fin = 6u << 20;                               // 24 MB freed by finalizers > 352 / 16 = 22 MB
    maxLa = 100u << 20;
    GwWhy(969, 352u << 20, 0, why, sizeof(why));
    assert(strstr(why, "enough large left yes") && strstr(why, "REFUSE"));
    fin = 0;
    assert(GwRunSite(0x2000, 0x10) == 1 && g_gwFakeAsk == 0x2000 && g_gwFakeHeap == 384u << 20);
    assert(k.gwExpands == 1 && k.gwKB == 32 * 1024 && k.gwMaxAskKB == 32 * 1024 && k.gwLines == 1 && k.gwFails == 0);
    assert(k.gcExpandStreak == 1 && k.gcExpandStreakMax == 1);
    assert(GwRunSite(3, 3) == 0 && k.gwFails == 1 && k.gwKB == 32 * 1024);
    g_gwFakeHeap += 1u << 20;  // growth not through our calls
    assert(GwRunSite(0x40, 0x40) == 1 && k.gwUnseenKB == 1024);
    assert(k.gcExpandStreak == 3 && k.gcExpandStreakMax == 3);  // same gc_no: one streak
    gcNo++;                                                   // a collection ends it
    assert(GwRunSite(0x40, 0x40) == 1 && k.gcExpandStreak == 1 && k.gcExpandStreakMax == 3);
    for (int i = 0; i < 60; i++) GwRunSite(0x40, 1);
    assert(k.gwExpands == 64 && k.gwLines == 50 && k.gcExpandStreakMax == 61);   // first 50 only
    assert(GwRunSite(0x4000, 0x4000) == 1 && k.gwLines == 51 && k.gwMaxAskKB == 64 * 1024);  // >= 64 MB always
    // under 600 MB so far (385 + 61 x 0.25 + 64 MB); crossing writes the last 20, logged or not
    while (g_gwFakeHeap <= (600u << 20)) GwRunSite(0x800, 0x800);
    assert(g_gwCrossed && k.gwLines == 51 + 20 && g_gwRingN > 20);
    assert(GwRunSite(0x800, 0x800) == 1 && k.gwLines == 71);  // crossing happens once
    k.gwMode = 0;
    assert(GwRunSite(0x4000, 0x4000) == 1 && k.gwLines == 71);  // switch off: count only
    // LoadXmlAsString: key visible to an expansion on this thread while it runs, string size counted after
    g_gwXmlOrig = GwFakeXml;
    g_gwFakeStr[1] = 3u << 20;  // EA MonoString length at +4: 3 M chars = 6 MB
    g_gwFakeStr[2] = 0x003F003C;  // "<?", what round 3 read as the length
    k.gwMode = 1;
    assert(GwXml(0x11111111, 0x22222222, 0x33, 0x0333E5C0) == (DWORD)(uintptr_t)g_gwFakeStr && g_gwXmlTid == 0);
    assert(g_gwXmlSeen[0] == 0x11111111 && g_gwXmlSeen[1] == 0x22222222 && g_gwXmlSeen[2] == 0x33 && g_gwXmlSeen[3] == 0x0333E5C0);
    assert(k.gwXmlCalls == 1 && k.gwXmlMaxKB == 6 * 1024);
    // GcBlackOk: every punt counted, the warning forwarded every old-interval-th time
    void* fakeWarn = (void*)GwFakeWarn;
    void* const* savedWarn = g_gwWarnProc;
    g_gwWarnProc = &fakeWarn, g_gwWarnEvery = 5, g_gwWarned = 0;
    for (int i = 0; i < 7; i++) GcBlackOk("Repeated allocation", 969u << 12);
    assert(k.gcBlackOk == 7 && k.gcBlackOkMaxKB == 3876 && g_gwWarned == 1);
    g_gwWarnProc = savedWarn, g_gwWarnEvery = 5;
    // window: off without a cap; with a cap but other bytes at the sites (no TS3W here) nothing switches
    GcCapWindow(true, "test");
    assert(!g_gcStrict && k.gcStrictN == 0);
    g_gcCapBytes = 1u << 20;
    GcCapWindow(true, "test");
    assert(!g_gcStrict && k.gcStrictN == 0 && k.gcLenientN == 0 && k.gcSwitchSec == 0);
    GcCapWindow(false, "test");  // already lenient: no-op
    assert(!g_gcStrict && k.gcLenientN == 0);
    // streak guard: the 3rd expansion with no collection lets the punt take the request, a collection arms it again
    gcNo++, bl = 1u << 20;
    assert(GwRunSite(0x2000, 206) == 1 && GwRunSite(0x2000, 206) == 1 && k.gcExpandStreak == 2 && bl == 1u << 20);
    assert(GwRunSite(0x2000, 206) == 1 && k.gcExpandStreak == 3 && bl == 0x3000);
    gcNo++, bl = 1u << 20;
    assert(GwRunSite(0x2000, 206) == 1 && k.gcExpandStreak == 1 && bl == 1u << 20);
    // gcstreakat: another streak moves it (guard off, test stop), 0 turns it off
    gcNo++;
    ts3native_gc_streak_at = 5;
    for (int i = 0; i < 4; i++) GwRunSite(0x2000, 206);
    assert(k.gcExpandStreak == 4 && bl == 1u << 20);
    assert(GwRunSite(0x2000, 206) == 1 && k.gcExpandStreak == 5 && bl == 0x3000);
    gcNo++, bl = 1u << 20;
    ts3native_gc_streak_at = 0;
    for (int i = 0; i < 6; i++) GwRunSite(0x2000, 206);
    assert(k.gcExpandStreak == 6 && bl == 1u << 20);
    ts3native_gc_streak_at = GW_STREAK_PUNT;
    g_gcCapBytes = 0;  // no cap, no guard
    gcNo++;
    for (int i = 0; i < 3; i++) GwRunSite(0x2000, 206);
    assert(k.gcExpandStreak == 3 && bl == 1u << 20);
    g_mx = saved, g_gwb = savedB, g_gwLargeFree = GW_LARGE_FREE, g_gwExpand = (GwExpandFn)(uintptr_t)GW_EXPAND, g_gwLastHeap = 0;
    g_gwXmlOrig = NULL, g_gwRingN = 0, g_gwCrossed = false, g_gwXmlLines = 0, g_gwStreakGc = 0xFFFFFFFF;
    memset(&k.gwMode, 0, sizeof(LONG) * 18);
    printf("GcGrowLog: thunk passes blocks and needed (esi kept), heap growth, failures, unseen growth, 50-line limit, >= 64 MB lines, "
           "the last 20 at 600 MB once, refusal text (large bytes left, finalizer bytes), xml key and size (+4), gwMode 0 counts only, expand streak per gc_no, black-list punt count: ok\n");
}
