// TestHitchAttr ([HitchAttr], hitchattr.cpp, docs/96 §9). Included by selftest.cpp.
static void TestHitchAttr() {
    Ts3nCounters& k = ts3native_counters;
    static_assert(offsetof(Ts3nCounters, haReady) == 0x30C4 && offsetof(Ts3nCounters, haWaits) == 0x30C8 &&
                  offsetof(Ts3nCounters, haUs) == 0x30D8 && offsetof(Ts3nCounters, haMaxUs) == 0x30E8 &&
                  sizeof(Ts3nCounters) == 6314 * 4, "counters v66 layout after v65 (docs/registry.md)");
    static_assert(offsetof(HaFrame, us) == 0x08 && offsetof(HaFrame, n) == 0x18 && offsetof(HaFrame, maxKey) == 0x20 &&
                  offsetof(HaFrame, mvUs) == 0x24 && offsetof(HaFrame, mvKey) == 0x34 && sizeof(HaFrame) == 0x38,
                  "ts3native_hitchattr layout (stutter-profile.js HA_*)");
    assert(k.version == 912 && ts3native_hitchattr.version == 2);
    HaFrame& f = ts3native_hitchattr;
    const DWORD ownerSave = g_async.owner;
    const LONG enSave = f.enabled;
    memset(f.us, 0, sizeof(f.us)), f.n = f.maxUs = f.maxKey = 0;
    const LONG w0 = k.haWaits[HA_LOCK], u0 = k.haUs[HA_LOCK], j0 = k.haUs[HA_JOB];

    g_async.owner = GetCurrentThreadId();
    f.enabled = 0;
    HaNote(0x0085FE10, 500, HA_LOCK);  // off: nothing
    assert(f.n == 0 && k.haWaits[HA_LOCK] == w0);
    f.enabled = 1;
    HaNote(0x0085FE10, 500, HA_LOCK);
    HaNote(0x0062EF7A, 2000, HA_LOCK);
    HaNote(0x02000003, 1500, HA_JOB);
    HaNote(0x05000000, 0, HA_PLANWAIT);  // no time: not a wait
    assert(f.us[HA_LOCK] == 2500 && f.us[HA_JOB] == 1500 && f.us[HA_PLANWAIT] == 0 && f.n == 3);
    assert(f.maxUs == 2000 && f.maxKey == 0x0062EF7A);
    assert(k.haWaits[HA_LOCK] == w0 + 2 && k.haUs[HA_LOCK] == u0 + 2500 && k.haUs[HA_JOB] == j0 + 1500 && k.haMaxUs >= 2000);
    g_async.owner = GetCurrentThreadId() + 1;  // not the job owner: not the sim's wait
    HaNote(0x0085FE10, 9000, HA_LOCK);
    assert(f.n == 3 && f.maxUs == 2000);

    g_async.owner = ownerSave;
    f.enabled = enSave;
    memset(f.us, 0, sizeof(f.us)), f.n = f.maxUs = f.maxKey = 0;
    printf("HitchAttr: per-frame sim waits by class and largest key, owner only, off by default: ok\n");

    // JobNameMs (docs/247 §12.4): the line names the job, off by default, 20 a minute
    static_assert(offsetof(Ts3nCounters, rjnMinUs) == 0x5E40 && offsetof(Ts3nCounters, rjnDropped) == 0x5E48, "counters v173 rjn* layout");
    char b[512];
    const RjnSum s = {0x1A2B3C4D, 77, 2, 1, 2, 775000, 610000, 120000, 9000, 3, 1, 300000};
    RjnText(b, sizeof(b), 0x02000003, 775400, 0x1A2B3C4D, s, "on", 1234);
    assert(!strcmp(b, "rtjob wait: 775 ms key 02000003, job route 1A2B3C4D seq 77 foreign, IC kind 2, scope unseen, job 775 ms plan 610 host 120 "
                      "other 45, plans 3 nodes 9000, deadline 300 ms, capped, arm on, at 1234 ms"));
    RjnText(b, sizeof(b), 0x06000000, 400000, 0x55, s, "-", 1);
    assert(strstr(b, "last job route 1A2B3C4D (waited for 00000055)"));
    const RjnSum sSave = g_rjnLast;
    const LONG minSave = k.rjnMinUs, l0 = k.rjnLines, d0 = k.rjnDropped;
    g_rjnLast = s;
    k.rjnMinUs = 0;
    RjnWait(0x02000003, 900000, s.handle);  // off: nothing
    assert(k.rjnLines == l0 && k.rjnDropped == d0);
    k.rjnMinUs = 200000;
    RjnWait(0x02000003, 199999, s.handle);  // under the bar
    assert(k.rjnLines == l0);
    for (int i = 0; i < 25; i++) RjnWait(0x02000003, 250000, s.handle);
    assert(k.rjnLines - l0 + k.rjnDropped - d0 == 25 && k.rjnLines - l0 <= 20 && k.rjnDropped - d0 >= 5);
    k.rjnMinUs = minSave, g_rjnLast = sSave;
    printf("HitchAttr JobNameMs: rtjob wait line names the job, off by default, capped 20 a minute: ok\n");
}
