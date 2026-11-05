// TestHitchAttr ([HitchAttr], hitchattr.cpp, docs/96 §9). Included by selftest.cpp.
static void TestHitchAttr() {
    Ts3nCounters& k = ts3native_counters;
    static_assert(offsetof(Ts3nCounters, haReady) == 0x30C4 && offsetof(Ts3nCounters, haWaits) == 0x30C8 &&
                  offsetof(Ts3nCounters, haUs) == 0x30D8 && offsetof(Ts3nCounters, haMaxUs) == 0x30E8 &&
                  sizeof(Ts3nCounters) == 5909 * 4, "counters v66 layout after v65 (docs/registry.md)");
    static_assert(offsetof(HaFrame, us) == 0x08 && offsetof(HaFrame, n) == 0x18 && offsetof(HaFrame, maxKey) == 0x20 &&
                  offsetof(HaFrame, mvUs) == 0x24 && offsetof(HaFrame, mvKey) == 0x34 && sizeof(HaFrame) == 0x38,
                  "ts3native_hitchattr layout (stutter-profile.js HA_*)");
    assert(k.version == 908 && ts3native_hitchattr.version == 2);
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
}
