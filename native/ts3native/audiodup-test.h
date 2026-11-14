// TestAudioStateDup ([AudioStateDup], docs/549 §5): a fake state with EA's 53-bucket table at +0x44 (chain through +0x00,
// key low +0x08, key high +0x0C) and a fake Add standing in for EA's (it links the node when the key is new, as Insert does).

static LONG g_adtAdds;
static u8* g_adtPool;
static int g_adtUsed;
static void __fastcall AdFakeAdd(u8* state, void*, DWORD keyLo, DWORD keyHi, DWORD value) {
    g_adtAdds++;
    u8* n = g_adtPool + 48 * g_adtUsed++;  // EA pops a node for every call, linked or not
    *(DWORD*)(n + 8) = keyLo, *(DWORD*)(n + 12) = keyHi, *(DWORD*)(n + 20) = value;
    if (AdHasKey(state, keyLo, keyHi) == 1) return;  // EA's duplicate path: node dropped
    u8** b = (u8**)(state + AD_BUCKETS + 4 * (keyLo % AD_NBUCKETS));
    *(u8**)n = *b, *b = n;
}

static void TestAudioStateDup() {
    static_assert(offsetof(Ts3nCounters, adMode) == 0x6304 && offsetof(Ts3nCounters, adSkipped) == 0x6310 && sizeof(Ts3nCounters) == 6357 * 4,
                  "counters v919 layout after v198 (docs/registry.md)");
    static u8 state[0x200], pool[48 * 64];
    const AdAddFn sAdd = g_adAdd;
    const bool sFix = g_adFix;
    Ts3nCounters& k = ts3native_counters;
    g_adAdd = AdFakeAdd, g_adtPool = pool;

    // (a) empty table, new keys, same bucket (low 1 and 54): no duplicate, EA runs
    memset(state, 0, sizeof(state)), g_adtUsed = 0, g_adtAdds = 0, g_adFix = false;
    k.adCalls = k.adDups = k.adSkipped = 0;
    assert(AdHasKey(state, 1, 0) == 0);
    AdAddHook(state, NULL, 1, 0, 0x10);
    AdAddHook(state, NULL, 54, 0, 0x11);  // 54 % 53 == 1: second node in the same chain
    AdAddHook(state, NULL, 1, 7, 0x12);   // same low word, other high word: a new key
    assert(g_adtAdds == 3 && k.adCalls == 3 && k.adDups == 0 && AdHasKey(state, 54, 0) == 1 && AdHasKey(state, 1, 7) == 1);

    // (b) Fix=0: a duplicate is counted and EA still runs (and loses its node)
    AdAddHook(state, NULL, 54, 0, 0x20);
    assert(g_adtAdds == 4 && g_adtUsed == 4 && k.adDups == 1 && k.adSkipped == 0);

    // (c) Fix=1: a duplicate is counted and skipped; the existing node keeps its value, no node used
    g_adFix = true;
    AdAddHook(state, NULL, 1, 0, 0x30);
    assert(g_adtAdds == 4 && g_adtUsed == 4 && k.adDups == 2 && k.adSkipped == 1);
    const u8* n = *(u8* const*)(state + AD_BUCKETS + 4 * 1);
    for (; n && !(*(const DWORD*)(n + 8) == 1 && *(const DWORD*)(n + 12) == 0); n = *(u8* const*)n) {}
    assert(n && *(const DWORD*)(n + 20) == 0x10);
    AdAddHook(state, NULL, 2, 0, 0x31);  // a new key still goes to EA with Fix=1
    assert(g_adtAdds == 5 && k.adCalls == 6);

    // (d) an endless chain is not ours to judge: -1, EA runs
    memset(state, 0, sizeof(state));
    u8* loop = pool;
    *(u8**)loop = loop, *(DWORD*)(loop + 8) = 99;
    *(u8**)(state + AD_BUCKETS + 4 * 3) = loop;
    assert(AdHasKey(state, 3, 0) == -1);

    g_adAdd = sAdd, g_adFix = sFix;
    k.adCalls = k.adDups = k.adSkipped = 0;
    printf("selftest audiodup ok (bucket walk, Fix=0 counts, Fix=1 skips and keeps the value, endless chain left to EA)\n");
}
